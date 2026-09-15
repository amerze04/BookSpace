using BookSpace.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BookSpace.Infrastructure.Persistence;

// IUnitOfWork over EF Core (WP-4 Phase 1b). The first explicit transaction in
// this codebase — WP-2 could record that none existed, because until booking
// creation every handler's changes went through a single SaveChangesAsync.
//
// **The execution strategy is not optional here** (CLAUDE.md §5).
// EnableRetryOnFailure is configured with 1205 in the retryable set, and with
// that on, calling BeginTransaction directly throws: EF refuses to let a
// transaction span retries it cannot replay. So the transaction goes *inside*
// the strategy, and the strategy re-runs the whole delegate.
//
// That retry is the point rather than a formality. dbo.CreateBooking takes
// U-mode key-range locks, which normally make concurrent bookings on one
// resource block rather than deadlock — but the blackout re-check reads
// BlackoutPeriods after Bookings while the blackout cascade writes them in the
// opposite order, so 1205 is a real outcome under contention. Retrying is what
// turns it into a slower success instead of a 500.
//
// **The delegate must be safe to run more than once.** Two things follow, both
// on the caller:
//
//   - the booking's Guid is chosen before ExecuteAsync is called, so a retry
//     re-inserts the same row rather than a second one;
//   - anything read to make a decision should be read inside the delegate, since
//     a retry is happening precisely because the data moved.
//
// One EF caveat, stated rather than hidden: if SaveChangesAsync succeeds and the
// commit then fails — a dropped connection after the server has already
// committed, before this client learns the outcome — the tracked entities are
// already marked Unchanged, so a retry through this class alone would not
// re-insert them. EF has no general answer to that ambiguity, and this class
// still doesn't try to be one (no verifySucceeded callback, no idempotency
// table) — but for this codebase's one caller, the ambiguity is closed a
// different way: BookingRepository.CreateAsync catches the PK violation a
// retried-but-already-committed dbo.CreateBooking call produces (the booking's
// own Guid is its natural idempotency key, chosen before ExecuteAsync runs, as
// below) and reads the row back rather than letting the violation surface. See
// ReadBackAlreadyCreatedAsync there for the mechanism and its own boundary —
// it distinguishes a genuine retry from an actual id collision rather than
// papering over both the same way.
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly BookSpaceDbContext _context;

    public UnitOfWork(BookSpaceDbContext context)
    {
        _context = context;
    }

    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var strategy = _context.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(token);

                var result = await work(token);

                // Bug fix, found alongside BookingRepository's own: dbo.CreateBooking
                // runs under SET XACT_ABORT ON, so a runtime error inside it — a PK
                // violation on a retried-but-already-committed insert, specifically —
                // makes SQL Server roll back this transaction itself, without waiting
                // to be asked. BookingRepository.CreateAsync's PK-violation handler
                // already accounts for that (it reads the already-existing row back
                // with no transaction, rather than the one this method opened, which
                // is dead by then) and returns normally instead of throwing — so
                // work() above can complete successfully with the transaction already
                // gone. Committing it anyway would throw ("This SqlTransaction has
                // completed; it is no longer usable"), for an operation whose result
                // is already correct and already durable. Connection is null exactly
                // when the server has already ended the transaction out from under
                // the client (EF/ADO.NET's own signal for it); there is nothing left
                // to commit in that case, and skipping it is what makes the read-back
                // path actually reach the caller instead of failing one step later.
                if (transaction.GetDbTransaction().Connection is not null)
                {
                    await transaction.CommitAsync(token);
                }

                return result;
            },
            cancellationToken);
    }
}
