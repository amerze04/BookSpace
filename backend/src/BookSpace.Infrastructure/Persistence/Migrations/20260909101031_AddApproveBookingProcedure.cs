using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // WP-5 Phase 3. FR-7.5 / AC-5: approving a since-taken slot must fail
    // safely, which means the same guarantee dbo.CreateBooking gives a create
    // has to apply to an approval too. Decision 0023 is inherited whole here —
    // same locks, same order, same index — see
    // docs/decisions/0023-booking-concurrency-strategy.md and
    // docs/wp5-plan.md §5.3 for the reasoning this file only summarises.
    //
    // The one structural difference from dbo.CreateBooking: the row already
    // exists. There is no INSERT — the overlap read excludes this booking's
    // own row by id instead of counting a row about to be added, and the
    // outcome is an UPDATE rather than an INSERT.
    public partial class AddApproveBookingProcedure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE PROCEDURE dbo.ApproveBooking
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

                    -- Reads and locks the row being decided in one statement, so
                    -- the Status = 'Pending' guard is atomic with the lock rather
                    -- than a separate check a second decision could slip between.
                    -- WITH (UPDLOCK) is a point lookup by primary key — no HOLDLOCK
                    -- needed, because there is no gap to protect here, only this
                    -- one existing row, held until the transaction ends so a
                    -- second concurrent decision on the *same* booking (two
                    -- approvers, or an approve racing a cancel) blocks rather than
                    -- both reading Pending and both proceeding.
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

                    -- Same fail-closed guard as dbo.CreateBooking (decision 0023):
                    -- Security.TenantAccessPolicy filters this SELECT, so a
                    -- connection with no tenant session context sees no resource
                    -- here either and fails closed rather than approving blind.
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

                    -- FR-3.4 / decision 0001, re-checked exactly as
                    -- dbo.CreateBooking does and for the identical race: a
                    -- blackout cascade can run between this procedure's checks
                    -- and its UPDATE and miss this booking if it only looked at
                    -- what existed a moment ago.
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

                    -- ==========================================================
                    -- The lock. Identical shape to dbo.CreateBooking's — same
                    -- hints, same index, same peak arithmetic (decision 0023) —
                    -- with one difference: this booking's own row already holds
                    -- its Quantity, so it is excluded by id (AND b.Id <>
                    -- @BookingId) rather than being the row about to be inserted.
                    -- ==========================================================
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

                    -- Split on what is left, matching dbo.CreateBooking exactly —
                    -- this is the race wp5-plan.md §5.3 names: a Pending booking
                    -- already holds its own claim, so the only way this branch is
                    -- reached is a *different*, still-live booking taking the
                    -- slot after this one went Pending (typically because
                    -- something else was cancelled and re-booked in between).
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
            migrationBuilder.Sql("DROP PROCEDURE dbo.ApproveBooking;");
        }
    }
}
