using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, P1/2. Closes the other half of the ambiguous-commit gap
    // BookingRepository.ReadBackAlreadyCreatedAsync closes in C#: on an
    // exclusive (or already-full) resource, a retry of an *already-committed*
    // dbo.CreateBooking call never even reached the INSERT/PK-violation this
    // migration's sibling reads back from — the overlap query counted the
    // existing row (inserted by the attempt whose acknowledgment was lost) as
    // an *additional* claim against its own retry, so the capacity check
    // itself refused with SlotUnavailable before the PK constraint ever had a
    // chance to fire.
    //
    // The fix is the one dbo.ApproveBooking already uses for the identical
    // reason: exclude the row whose id matches @BookingId from the overlap
    // sum (`AND b.Id <> @BookingId`). On a genuinely new booking this is a
    // no-op — no row with that id exists yet. On a retry it makes the check
    // ask the right question: "is there still room for the booking that was
    // already granted this identity", not "is there room for one more, on top
    // of the one that already exists". The retry's INSERT then reaches the PK
    // violation dbo.CreateBooking's C# caller already handles.
    public partial class AlterCreateBookingProcedureIdempotentRetry : Migration
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

                    -- P1/2: excludes a row that already carries this exact
                    -- @BookingId — see this migration's header. A no-op on a
                    -- genuinely new booking; on a retry of an already-committed
                    -- insert, it stops that row counting against its own retry.
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
