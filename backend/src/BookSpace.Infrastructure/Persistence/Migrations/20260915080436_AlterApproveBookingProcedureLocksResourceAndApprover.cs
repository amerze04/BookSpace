using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, items 3 and 4.
    //
    // Item 3's counterpart: the Resources read here (capacity/IsArchived) was
    // unlocked for the same reason dbo.CreateBooking's was — see
    // AlterCreateBookingProcedureLocksResourceRow's header. Same fix, same
    // reasoning: WITH (HOLDLOCK), since this transaction never itself writes
    // Resources.
    //
    // Item 4: AlterApproveBookingProcedureEligibilityRecheck's own comment
    // already says its ResourceApprovers re-check is "as close to 'atomic
    // with the decision' as this system gets" — honest about not being
    // watertight, because that SELECT held no lock either, leaving a window
    // between the check and this procedure's own COMMIT in which a concurrent
    // DELETE from ResourceApprovers (an admin revoking the caller) could still
    // slip in and commit. WITH (HOLDLOCK) on that check closes it the same
    // way: a shared lock held to end-of-transaction is incompatible with the
    // exclusive lock the DELETE needs, so the DELETE now blocks until this
    // transaction is done. RejectBookingCommandRequestHandler's own C#-side
    // re-check has no equivalent lock available to it outside a stored
    // procedure — its header already says so — and that residual, smaller gap
    // is accepted and documented there rather than fixed here.
    public partial class AlterApproveBookingProcedureLocksResourceAndApprover : Migration
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
                        SELECT 1 FROM dbo.ResourceApprovers
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
                    FROM dbo.Resources AS r
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
