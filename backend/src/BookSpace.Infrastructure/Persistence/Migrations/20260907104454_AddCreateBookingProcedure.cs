using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // WP-4 Phase 1b. The procedure CLAUDE.md §4.1 has named since WP-1 and that
    // has not existed until now: **the only place the no-double-booking
    // guarantee lives** (FR-4.2, AC-1). Hand-written, like the RLS migration —
    // EF will not scaffold a procedure, so `dotnet ef migrations add` produces an
    // empty Up/Down here and the body is written by hand (CLAUDE.md §5).
    //
    // The full reasoning, the rejected alternatives and the measured behaviour
    // are in docs/decisions/0023-booking-concurrency-strategy.md. The comments
    // below cover only what a reader of this file needs to not break it.
    public partial class AddCreateBookingProcedure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE PROCEDURE dbo.CreateBooking
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

                    -- Any error aborts the whole transaction rather than leaving a
                    -- doomed one open for the caller to trip over. Required for the
                    -- nesting below to be safe.
                    SET XACT_ABORT ON;

                    -- Only the two statuses that hold a claim on the resource can be
                    -- created. Everything else is a transition, not a creation, and
                    -- reaching this procedure with one is a programming error rather
                    -- than a rejection a client should see a reason code for.
                    IF @Status NOT IN ('Pending', 'Confirmed')
                    BEGIN
                        THROW 50000, 'dbo.CreateBooking accepts only Pending or Confirmed.', 1;
                    END

                    -- Joins the caller's transaction when there is one, so the
                    -- ApprovalRequest and Notifications rows EF writes afterwards
                    -- commit with this booking or not at all (IUnitOfWork). Only a
                    -- transaction this procedure opened is one it may end: rolling
                    -- back the caller's would destroy work it cannot see.
                    DECLARE @ownTransaction BIT = 0;

                    IF @@TRANCOUNT = 0
                    BEGIN
                        SET @ownTransaction = 1;
                        BEGIN TRANSACTION;
                    END

                    DECLARE @orgId      UNIQUEIDENTIFIER;
                    DECLARE @capacity   INT;
                    DECLARE @isArchived BIT;

                    -- Read through the RLS-filtered table, and **this is a safety
                    -- check, not a convenience**. Security.TenantAccessPolicy is a
                    -- filter policy: it filters the SELECTs below but does not block
                    -- this INSERT. A connection with no tenant session context
                    -- therefore sees zero Bookings rows, which would make the
                    -- capacity check pass trivially and overbook — a lock correctly
                    -- taken over an empty set. Because Resources is filtered by the
                    -- same predicate, such a connection cannot see the resource
                    -- either, so the procedure fails closed here instead.
                    SELECT
                        @orgId      = r.OrgId,
                        @capacity   = r.Capacity,
                        @isArchived = r.IsArchived
                    FROM dbo.Resources AS r
                    WHERE r.Id = @ResourceId;

                    IF @orgId IS NULL
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

                    -- FR-3.4 / decision 0001. Checked here as well as in the
                    -- application layer, and the reason is a real race rather than
                    -- belt and braces: between the handler's check and this INSERT,
                    -- an admin's blackout cascade can select the bookings to cancel
                    -- and miss this one, because it does not exist yet. The result
                    -- would be a live booking inside a blackout, which decision 0001
                    -- says must never exist.
                    --
                    -- HOLDLOCK alone, not UPDLOCK: this only has to stop a blackout
                    -- being inserted into the range while the transaction runs, and
                    -- the shared range locks two concurrent bookings take here are
                    -- compatible with each other. IX_BlackoutPeriods_Resource_Start
                    -- keeps the range narrow.
                    --
                    -- Half-open overlap on both sides, the same predicate
                    -- BlackoutPeriod.Overlaps uses: a blackout starting exactly when
                    -- the booking ends does not overlap it.
                    IF EXISTS (
                        SELECT 1
                        FROM dbo.BlackoutPeriods AS bp WITH (HOLDLOCK)
                        WHERE bp.ResourceId = @ResourceId
                          AND bp.StartsAtUtc < @EndsAtUtc
                          AND bp.EndsAtUtc   > @StartsAtUtc)
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;
                        SELECT 'BlackoutPeriod' AS ResultCode, NULL AS RemainingCapacity;
                        RETURN;
                    END

                    -- ==========================================================
                    -- The lock. Everything above is a guard; this is the
                    -- guarantee (FR-4.2, AC-1).
                    --
                    -- UPDLOCK **and** HOLDLOCK, and both are load-bearing:
                    --
                    --   HOLDLOCK makes these key-range locks, held to the end of
                    --   the transaction, so the range is protected against rows
                    --   that do not exist yet. Without it a second transaction
                    --   inserts into a gap this one already counted — the phantom
                    --   that is the whole problem.
                    --
                    --   UPDLOCK makes them U-mode. Two concurrent bookings on one
                    --   resource then *block* each other and run one after the
                    --   other, the loser seeing the winner's committed row. With
                    --   HOLDLOCK alone they would take compatible S-range locks,
                    --   both pass the check, and then deadlock on the inserts —
                    --   correct, but only via 1205 on the normal path.
                    --
                    -- IX_Bookings_Resource_Start is what keeps the locked range to
                    -- one resource instead of the table (CLAUDE.md §4.1 calls it
                    -- load-bearing; this is why). It INCLUDEs EndsAtUtc, Status and
                    -- Quantity, so this reads from the index alone.
                    --
                    -- Pending and Confirmed only: the other four statuses hold no
                    -- claim (decision 0005), which is the same live set the
                    -- availability query counts.
                    -- ==========================================================
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

                    -- **Peak concurrent units, not the sum of the overlapping ones.**
                    -- CLAUDE.md §4.1 said "sum" until 2026-09-07 and it was wrong:
                    -- with capacity 2 and bookings 09:00-10:00 and 10:00-11:00 of one
                    -- unit each, a request for 09:30-10:30 overlaps both, but the two
                    -- never coexist, so one unit is free throughout and the booking is
                    -- legal. Summing refuses it — and the availability query, which
                    -- sweeps properly, would already have offered the slot.
                    --
                    -- The peak of a step function is at a step, so only two kinds of
                    -- instant can hold it: the request's own start, and the start of
                    -- each overlapping booking inside the request. This is the same
                    -- arithmetic as CapacitySweep in BookSpace.Domain, reduced to the
                    -- single worst case.
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

                    -- Split on what is left, not on the resource (owner's call,
                    -- 2026-09-07). An exclusive resource can only ever produce
                    -- SlotUnavailable, because Capacity 1 admits no quantity but 1.
                    IF @remaining < @Quantity
                    BEGIN
                        IF @ownTransaction = 1 ROLLBACK TRANSACTION;

                        SELECT
                            CASE WHEN @remaining <= 0 THEN 'SlotUnavailable' ELSE 'CapacityExceeded' END
                                AS ResultCode,
                            @remaining AS RemainingCapacity;
                        RETURN;
                    END

                    -- OrgId comes from the resource, never from the caller: decision
                    -- 0006's FK_Bookings_Resources_SameOrg would refuse a mismatch
                    -- anyway, and taking it from the row makes the two physically
                    -- unable to disagree in the first place.
                    INSERT INTO dbo.Bookings
                        (Id, OrgId, ResourceId, UserId, RecurrenceRuleId,
                         StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                         CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                    VALUES
                        (@BookingId, @orgId, @ResourceId, @UserId, @RecurrenceRuleId,
                         @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @Status,
                         @NowUtc, @CreatedByUserId, @NowUtc, @CreatedByUserId);

                    IF @ownTransaction = 1 COMMIT TRANSACTION;

                    SELECT 'Created' AS ResultCode, @remaining - @Quantity AS RemainingCapacity;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE dbo.CreateBooking;");
        }
    }
}
