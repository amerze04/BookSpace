using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, item 3. The previous migration
    // (AlterCreateBookingProcedureRequiresApprovalRecheck) moved the
    // RequiresApproval/IsArchived read to "moments before the insert", inside
    // the transaction — closing the version of this bug where the decision
    // was made in C# before the transaction even opened. But that SELECT took
    // no lock, and several statements (the blackout check, the overlap scan)
    // run between it and the final INSERT/COMMIT — a window in which a
    // concurrent Archive or RequiresApproval UPDATE on the same Resources row
    // is not blocked by anything this procedure holds, and can still commit
    // before this transaction does, leaving the booking created against a
    // snapshot that is already stale by the time it lands.
    //
    // The fix is the smallest one that actually closes it: WITH (HOLDLOCK) on
    // the Resources read. This transaction never itself writes Resources, so
    // a plain shared lock held to end-of-transaction is enough — it is
    // incompatible with the exclusive lock any concurrent UPDATE needs, so
    // that UPDATE now blocks until this transaction commits or rolls back,
    // the same technique this procedure already uses for BlackoutPeriods.
    // UPDLOCK is not needed: that hint exists to avoid conversion deadlocks
    // when a transaction later upgrades its own shared lock to exclusive, and
    // this one never does.
    //
    // This does not change decision 0023's reasoning for reading Resources
    // *at all* (the RLS fail-open guard — no session context means zero
    // overlapping rows and a false "available", closed by reading Resources
    // through the filtered table first) — it only changes the SELECT from
    // unlocked to locked, so a concurrent write can no longer slip in after
    // it.
    public partial class AlterCreateBookingProcedureLocksResourceRow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.CreateBooking
                    @BookingId        UNIQUEIDENTIFIER,
                    @ResourceId       UNIQUEIDENTIFIER,
                    @UserId           UNIQUEIDENTIFIER,
                    @StartsAtUtc      DATETIME2(0),
                    @EndsAtUtc        DATETIME2(0),
                    @Quantity         INT,
                    @Status           NVARCHAR(20),
                    @CreatedByUserId  UNIQUEIDENTIFIER,
                    @NowUtc           DATETIME2(0),
                    @RecurrenceRuleId UNIQUEIDENTIFIER = NULL,
                    @Title            NVARCHAR(200)    = NULL
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    IF @Status NOT IN ('Pending', 'Confirmed')
                    BEGIN
                        THROW 50000, 'dbo.CreateBooking accepts only Pending or Confirmed.', 1;
                    END

                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @orgId            UNIQUEIDENTIFIER;
                    DECLARE @capacity         INT;
                    DECLARE @isArchived       BIT;
                    DECLARE @requiresApproval BIT;

                    -- Hardening pass, item 3: WITH (HOLDLOCK) — see this
                    -- migration's header. Blocks a concurrent Archive/
                    -- RequiresApproval/Capacity UPDATE on this row until this
                    -- transaction commits or rolls back.
                    SELECT
                        @orgId            = r.OrgId,
                        @capacity         = r.Capacity,
                        @isArchived       = r.IsArchived,
                        @requiresApproval = r.RequiresApproval
                    FROM dbo.Resources AS r WITH (HOLDLOCK)
                    WHERE r.Id = @ResourceId;

                    IF @orgId IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceNotFound' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    IF @isArchived = 1
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceArchived' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @ResourceId
                          AND bp.StartsAtUtc < @EndsAtUtc
                          AND bp.EndsAtUtc   > @StartsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    DECLARE @overlapping TABLE (
                        StartsAtUtc DATETIME2(0) NOT NULL,
                        EndsAtUtc   DATETIME2(0) NOT NULL,
                        Quantity    INT          NOT NULL);

                    INSERT INTO @overlapping (StartsAtUtc, EndsAtUtc, Quantity)
                    SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
                    FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ResourceId = @ResourceId
                      AND b.Id <> @BookingId
                      AND b.Status IN ('Pending', 'Confirmed')
                      AND b.StartsAtUtc < @EndsAtUtc
                      AND b.EndsAtUtc   > @StartsAtUtc;

                    DECLARE @peak INT;

                    SELECT @peak = ISNULL(MAX(u.Used), 0)
                    FROM (
                        SELECT @StartsAtUtc AS T
                        UNION
                        SELECT o.StartsAtUtc FROM @overlapping AS o WHERE o.StartsAtUtc > @StartsAtUtc
                    ) AS boundary
                    CROSS APPLY (
                        SELECT SUM(o.Quantity) AS Used
                        FROM @overlapping AS o
                        WHERE o.StartsAtUtc <= boundary.T
                          AND o.EndsAtUtc   >  boundary.T
                    ) AS u;

                    DECLARE @remaining INT = @capacity - @peak;

                    IF @remaining < @Quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity,
                            NULL AS ActualStatus;
                        RETURN;
                    END

                    DECLARE @finalStatus NVARCHAR(20) =
                        CASE WHEN @requiresApproval = 1 AND @Status = 'Confirmed'
                             THEN 'Pending'
                             ELSE @Status
                        END;

                    INSERT INTO dbo.Bookings
                        (Id, OrgId, ResourceId, UserId, RecurrenceRuleId,
                         StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                         CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                    VALUES
                        (@BookingId, @orgId, @ResourceId, @UserId, @RecurrenceRuleId,
                         @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @finalStatus,
                         @NowUtc, @CreatedByUserId, @NowUtc, @CreatedByUserId);

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Created' AS ResultCode, @remaining - @Quantity AS RemainingCapacity,
                        @finalStatus AS ActualStatus;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER PROCEDURE dbo.CreateBooking
                    @BookingId        UNIQUEIDENTIFIER,
                    @ResourceId       UNIQUEIDENTIFIER,
                    @UserId           UNIQUEIDENTIFIER,
                    @StartsAtUtc      DATETIME2(0),
                    @EndsAtUtc        DATETIME2(0),
                    @Quantity         INT,
                    @Status           NVARCHAR(20),
                    @CreatedByUserId  UNIQUEIDENTIFIER,
                    @NowUtc           DATETIME2(0),
                    @RecurrenceRuleId UNIQUEIDENTIFIER = NULL,
                    @Title            NVARCHAR(200)    = NULL
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    IF @Status NOT IN ('Pending', 'Confirmed')
                    BEGIN
                        THROW 50000, 'dbo.CreateBooking accepts only Pending or Confirmed.', 1;
                    END

                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @orgId            UNIQUEIDENTIFIER;
                    DECLARE @capacity         INT;
                    DECLARE @isArchived       BIT;
                    DECLARE @requiresApproval BIT;

                    SELECT
                        @orgId            = r.OrgId,
                        @capacity         = r.Capacity,
                        @isArchived       = r.IsArchived,
                        @requiresApproval = r.RequiresApproval
                    FROM dbo.Resources AS r
                    WHERE r.Id = @ResourceId;

                    IF @orgId IS NULL
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceNotFound' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    IF @isArchived = 1
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'ResourceArchived' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @ResourceId
                          AND bp.StartsAtUtc < @EndsAtUtc
                          AND bp.EndsAtUtc   > @StartsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity, NULL AS ActualStatus;
                        RETURN;
                    END

                    DECLARE @overlapping TABLE (
                        StartsAtUtc DATETIME2(0) NOT NULL,
                        EndsAtUtc   DATETIME2(0) NOT NULL,
                        Quantity    INT          NOT NULL);

                    INSERT INTO @overlapping (StartsAtUtc, EndsAtUtc, Quantity)
                    SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
                    FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ResourceId = @ResourceId
                      AND b.Id <> @BookingId
                      AND b.Status IN ('Pending', 'Confirmed')
                      AND b.StartsAtUtc < @EndsAtUtc
                      AND b.EndsAtUtc   > @StartsAtUtc;

                    DECLARE @peak INT;

                    SELECT @peak = ISNULL(MAX(u.Used), 0)
                    FROM (
                        SELECT @StartsAtUtc AS T
                        UNION
                        SELECT o.StartsAtUtc FROM @overlapping AS o WHERE o.StartsAtUtc > @StartsAtUtc
                    ) AS boundary
                    CROSS APPLY (
                        SELECT SUM(o.Quantity) AS Used
                        FROM @overlapping AS o
                        WHERE o.StartsAtUtc <= boundary.T
                          AND o.EndsAtUtc   >  boundary.T
                    ) AS u;

                    DECLARE @remaining INT = @capacity - @peak;

                    IF @remaining < @Quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity,
                            NULL AS ActualStatus;
                        RETURN;
                    END

                    DECLARE @finalStatus NVARCHAR(20) =
                        CASE WHEN @requiresApproval = 1 AND @Status = 'Confirmed'
                             THEN 'Pending'
                             ELSE @Status
                        END;

                    INSERT INTO dbo.Bookings
                        (Id, OrgId, ResourceId, UserId, RecurrenceRuleId,
                         StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                         CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                    VALUES
                        (@BookingId, @orgId, @ResourceId, @UserId, @RecurrenceRuleId,
                         @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @finalStatus,
                         @NowUtc, @CreatedByUserId, @NowUtc, @CreatedByUserId);

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Created' AS ResultCode, @remaining - @Quantity AS RemainingCapacity,
                        @finalStatus AS ActualStatus;
                END
                """);
        }
    }
}
