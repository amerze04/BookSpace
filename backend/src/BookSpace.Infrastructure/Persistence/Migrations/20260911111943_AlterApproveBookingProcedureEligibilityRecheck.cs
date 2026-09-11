using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, P2 — approver TOCTOU. Before this migration,
    // dbo.ApproveBooking used @ApproverUserId only to stamp UpdatedByUserId;
    // "is this caller still an eligible approver for this resource" was
    // checked exactly once, in ApproveBookingCommandRequestHandler, via
    // ApprovalReach — resolved *before* this procedure's own lock is taken.
    // An admin removing the caller from the resource's approver list in that
    // window (decision 0018) had no effect on an already-in-flight approval:
    // it would still commit.
    //
    // The fix re-reads ResourceApprovers under the same lock this procedure
    // already takes for capacity — atomic with the decision, not a separate
    // check a race could still slip between. It is conditional on
    // @CallerIsTenantAdmin: decision 0002 gives a TenantAdmin sweeping reach
    // that never depended on ResourceApprovers in the first place, so nothing
    // can go stale for them here — only the Approver-role path is racing a
    // mutable table, and only that path is re-verified.
    public partial class AlterApproveBookingProcedureEligibilityRecheck : Migration
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

                    -- P2 hardening: the eligibility re-check, atomic with the
                    -- lock this procedure already holds on the booking row.
                    -- Skipped entirely for a TenantAdmin, whose reach
                    -- (decision 0002) was never a function of this table.
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.ApproveBooking
                    @BookingId       UNIQUEIDENTIFIER,
                    @ApproverUserId  UNIQUEIDENTIFIER,
                    @NowUtc          DATETIME2(0)
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
