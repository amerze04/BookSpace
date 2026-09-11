using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, P0. Closes a race decision 0001's cascade did not
    // account for: CreateBlackoutPeriodCommandRequestHandler and
    // UpdateBlackoutPeriodCommandRequestHandler used to read "which bookings
    // does this blackout cancel" with no lock at all, before their own
    // SaveChangesAsync opened any transaction. A booking created by
    // dbo.CreateBooking in the gap between that read and the blackout's own
    // commit was never selected for cancellation and never would be — a live
    // Pending/Confirmed booking left inside a blackout, which decision 0001
    // says must never exist.
    //
    // The fix is not a second locking strategy: it is the *same* one.
    // dbo.LockBookingsForBlackout takes the identical UPDLOCK, HOLDLOCK range
    // lock, over the identical predicate and the identical index
    // (IX_Bookings_Resource_Start), that dbo.CreateBooking and
    // dbo.ApproveBooking already take before they decide anything. Whichever
    // side reaches that range first holds it until its own transaction ends;
    // the other blocks rather than acting on a snapshot that is about to go
    // stale. See docs/decisions/0023-booking-concurrency-strategy.md, which
    // this inherits rather than replaces.
    //
    // This procedure returns no rows — it is a lock, not a query. The two
    // blackout handlers call it inside IUnitOfWork.ExecuteAsync (joining the
    // same transaction dbo.CreateBooking's callers already use for exactly
    // this reason), then re-read the overlapping bookings themselves through
    // EF, now safe from the race.
    //
    // **Lock order.** This takes Bookings before writing BlackoutPeriods;
    // dbo.CreateBooking takes BlackoutPeriods (HOLDLOCK read) before Bookings.
    // That is a genuine inversion, and deliberately not a new one to solve —
    // it is the exact class 0023 already documents ("the blackout re-check
    // inverts lock order against decision 0001's cascade") and its measured
    // evidence already shows 1205 firing routinely under contention and being
    // fully absorbed by IUnitOfWork's retry. No change to dbo.CreateBooking or
    // dbo.ApproveBooking was needed or made.
    public partial class AddLockBookingsForBlackoutProcedure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE PROCEDURE dbo.LockBookingsForBlackout
                    @ResourceId  UNIQUEIDENTIFIER,
                    @StartsAtUtc DATETIME2(0),
                    @EndsAtUtc   DATETIME2(0)
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    -- This procedure only takes a lock; it commits nothing of
                    -- its own and must never be allowed to invent a
                    -- transaction whose boundaries the caller does not
                    -- control. IUnitOfWork.ExecuteAsync is the only caller,
                    -- and it always has one open already.
                    IF @@TRANCOUNT = 0
                    BEGIN
                        THROW 50001,
                            'dbo.LockBookingsForBlackout must run inside an existing transaction.', 1;
                    END

                    -- Identical hints, identical predicate, identical index to
                    -- the range lock dbo.CreateBooking and dbo.ApproveBooking
                    -- take over Bookings (CLAUDE.md §4.1, decision 0023) — that
                    -- congruence is what makes the two sides actually contend
                    -- for the same key range instead of merely resembling each
                    -- other. HOLDLOCK holds it to the end of the transaction and
                    -- covers the gaps as well as any matching rows, so a
                    -- concurrent dbo.CreateBooking cannot insert into this exact
                    -- range while a blackout transaction holds it, and vice
                    -- versa. UPDLOCK makes it U-mode, so two callers taking it
                    -- block rather than both proceeding and deadlocking on their
                    -- writes.
                    --
                    -- No result set: the caller re-reads the overlapping
                    -- bookings itself afterwards, through EF, now inside the
                    -- same lock this just took.
                    SELECT 1
                    FROM dbo.Bookings WITH (UPDLOCK, HOLDLOCK)
                    WHERE ResourceId = @ResourceId
                      AND Status IN ('Pending', 'Confirmed')
                      AND StartsAtUtc < @EndsAtUtc
                      AND EndsAtUtc   > @StartsAtUtc;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE dbo.LockBookingsForBlackout;");
        }
    }
}
