using BookSpace.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

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
// commit then fails, the tracked entities are already marked Unchanged, so a
// retry would not re-insert them. EF has no general answer to this and neither
// does this class; the window is the commit itself.
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

                await transaction.CommitAsync(token);

                return result;
            },
            cancellationToken);
    }
}
