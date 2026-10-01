using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, finding 3, FR-9.3. Adds one check, right after the
    // existing "is this booking still Pending" guard and before every other
    // rule: ApprovalRequests.ExpiresAtUtc is a real deadline, and a human
    // approving one second after it passed — but before the stale-approval
    // expiry job happens to sweep it — must be refused the same way the
    // sweep would refuse them, not silently approved into a state the sweep
    // would otherwise have raced to contradict.
    //
    // Needs no extra lock of its own: the procedure's very first statement
    // already takes UPDLOCK on this exact Bookings row and holds it for the
    // whole transaction, which already serializes against
    // StaleApprovalExpiryRepository.TryExpireAsync's own write to the same
    // row (its SaveChangesAsync would block behind this transaction's lock,
    // then fail its own RowVersion check once this one commits — the
    // existing, already-tested mechanism, not a new one). ExpiresAtUtc
    // itself is never updated after an ApprovalRequest is created, so a
    // plain read of it here is race-free on its own terms.
    public partial class AlterApproveBookingProcedureChecksApprovalExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.ApproveBooking
                    @BookingId           UNIQUEIDENTIFIER,
                    @ApproverUserId      UNIQUEIDENTIFIER,
                    @NowUtc              DATETIME2(0),
                    @CallerIsTenantAdmin BIT = 0
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @resourceId  UNIQUEIDENTIFIER;
                    DECLARE @quantity    INT;
                    DECLARE @startsAtUtc DATETIME2(0);
                    DECLARE @endsAtUtc   DATETIME2(0);

                    SELECT
                        @resourceId  = b.ResourceId,
                        @quantity    = b.Quantity,
                        @startsAtUtc = b.StartsAtUtc,
                        @endsAtUtc   = b.EndsAtUtc
                    FROM dbo.Bookings AS b WITH (UPDLOCK)
                    WHERE b.Id = @BookingId
                      AND b.Status = 'Pending';

                    IF @resourceId IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BookingNotPending' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    -- Hardening pass, finding 3: see this migration's own
                    -- header for why no extra lock is needed here — the
                    -- UPDLOCK above already serializes against the
                    -- stale-approval expiry job's own write to this row.
                    DECLARE @approvalExpiresAtUtc DATETIME2(0);

                    SELECT @approvalExpiresAtUtc = ar.ExpiresAtUtc
                    FROM dbo.ApprovalRequests AS ar
                    WHERE ar.BookingId = @BookingId;

                    IF @approvalExpiresAtUtc IS NOT NULL AND @approvalExpiresAtUtc <= @NowUtc
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ApprovalRequestExpired' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    -- Hardening pass, item 4: WITH (HOLDLOCK) — see this
                    -- migration's header. Blocks a concurrent DELETE from
                    -- ResourceApprovers (an admin revoking this approver)
                    -- until this transaction commits or rolls back.
                    IF @CallerIsTenantAdmin = 0 AND NOT EXISTS (
                        SELECT 1 FROM dbo.ResourceApprovers WITH (HOLDLOCK)
                        WHERE ResourceId = @resourceId AND UserId = @ApproverUserId)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ApproverNotEligible' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    DECLARE @capacity   INT;
                    DECLARE @isArchived BIT;

                    -- Hardening pass, item 3's counterpart: WITH (HOLDLOCK).
                    SELECT
                        @capacity   = r.Capacity,
                        @isArchived = r.IsArchived
                    FROM dbo.Resources AS r WITH (HOLDLOCK)
                    WHERE r.Id = @resourceId;

                    IF @capacity IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceNotFound' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF @isArchived = 1
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceArchived' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @resourceId
                          AND bp.StartsAtUtc < @endsAtUtc
                          AND bp.EndsAtUtc   > @startsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    DECLARE @overlapping TABLE (
                        StartsAtUtc DATETIME2(0) NOT NULL,
                        EndsAtUtc   DATETIME2(0) NOT NULL,
                        Quantity    INT          NOT NULL);

                    INSERT INTO @overlapping (StartsAtUtc, EndsAtUtc, Quantity)
                    SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
                    FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ResourceId = @resourceId
                      AND b.Id <> @BookingId
                      AND b.Status IN ('Pending', 'Confirmed')
                      AND b.StartsAtUtc < @endsAtUtc
                      AND b.EndsAtUtc   > @startsAtUtc;

                    DECLARE @peak INT;

                    SELECT @peak = ISNULL(MAX(u.Used), 0)
                    FROM (
                        SELECT @startsAtUtc AS T
                        UNION
                        SELECT o.StartsAtUtc FROM @overlapping AS o WHERE o.StartsAtUtc > @startsAtUtc
                    ) AS boundary
                    CROSS APPLY (
                        SELECT SUM(o.Quantity) AS Used
                        FROM @overlapping AS o
                        WHERE o.StartsAtUtc <= boundary.T
                          AND o.EndsAtUtc   >  boundary.T
                    ) AS u;

                    DECLARE @remaining INT = @capacity - @peak;

                    IF @remaining < @quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity;
                        RETURN;
                    END

                    UPDATE dbo.Bookings
                    SET Status          = 'Confirmed',
                        UpdatedAtUtc    = @NowUtc,
                        UpdatedByUserId = @ApproverUserId
                    WHERE Id = @BookingId;

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Approved' AS ResultCode, @remaining - @quantity AS RemainingCapacity;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.ApproveBooking
                    @BookingId           UNIQUEIDENTIFIER,
                    @ApproverUserId      UNIQUEIDENTIFIER,
                    @NowUtc              DATETIME2(0),
                    @CallerIsTenantAdmin BIT = 0
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @resourceId  UNIQUEIDENTIFIER;
                    DECLARE @quantity    INT;
                    DECLARE @startsAtUtc DATETIME2(0);
                    DECLARE @endsAtUtc   DATETIME2(0);

                    SELECT
                        @resourceId  = b.ResourceId,
                        @quantity    = b.Quantity,
                        @startsAtUtc = b.StartsAtUtc,
                        @endsAtUtc   = b.EndsAtUtc
                    FROM dbo.Bookings AS b WITH (UPDLOCK)
                    WHERE b.Id = @BookingId
                      AND b.Status = 'Pending';

                    IF @resourceId IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BookingNotPending' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF @CallerIsTenantAdmin = 0 AND NOT EXISTS (
                        SELECT 1 FROM dbo.ResourceApprovers WITH (HOLDLOCK)
                        WHERE ResourceId = @resourceId AND UserId = @ApproverUserId)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ApproverNotEligible' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    DECLARE @capacity   INT;
                    DECLARE @isArchived BIT;

                    SELECT
                        @capacity   = r.Capacity,
                        @isArchived = r.IsArchived
                    FROM dbo.Resources AS r WITH (HOLDLOCK)
                    WHERE r.Id = @resourceId;

                    IF @capacity IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceNotFound' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF @isArchived = 1
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceArchived' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @resourceId
                          AND bp.StartsAtUtc < @endsAtUtc
                          AND bp.EndsAtUtc   > @startsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    DECLARE @overlapping TABLE (
                        StartsAtUtc DATETIME2(0) NOT NULL,
                        EndsAtUtc   DATETIME2(0) NOT NULL,
                        Quantity    INT          NOT NULL);

                    INSERT INTO @overlapping (StartsAtUtc, EndsAtUtc, Quantity)
                    SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
                    FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ResourceId = @resourceId
                      AND b.Id <> @BookingId
                      AND b.Status IN ('Pending', 'Confirmed')
                      AND b.StartsAtUtc < @endsAtUtc
                      AND b.EndsAtUtc   > @startsAtUtc;

                    DECLARE @peak INT;

                    SELECT @peak = ISNULL(MAX(u.Used), 0)
                    FROM (
                        SELECT @startsAtUtc AS T
                        UNION
                        SELECT o.StartsAtUtc FROM @overlapping AS o WHERE o.StartsAtUtc > @startsAtUtc
                    ) AS boundary
                    CROSS APPLY (
                        SELECT SUM(o.Quantity) AS Used
                        FROM @overlapping AS o
                        WHERE o.StartsAtUtc <= boundary.T
                          AND o.EndsAtUtc   >  boundary.T
                    ) AS u;

                    DECLARE @remaining INT = @capacity - @peak;

                    IF @remaining < @quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity;
                        RETURN;
                    END

                    UPDATE dbo.Bookings
                    SET Status          = 'Confirmed',
                        UpdatedAtUtc    = @NowUtc,
                        UpdatedByUserId = @ApproverUserId
                    WHERE Id = @BookingId;

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Approved' AS ResultCode, @remaining - @quantity AS RemainingCapacity;
                END
                """);
        }
    }
}
