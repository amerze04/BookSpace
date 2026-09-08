namespace BookSpace.Application.Abstractions;

// One transactional boundary around work that spans more than one write
// mechanism (WP-4 Phase 1a). Declared here, implemented in
// BookSpace.Infrastructure, like every other port — CLAUDE.md §3 keeps EF Core
// out of BookSpace.Application entirely.
//
// **Why WP-4 is the first package to need one.** Until now every handler's
// changes went through a single SaveChangesAsync, which is already atomic, so
// WP-2 could record that "no explicit BeginTransaction calls exist anywhere
// yet". Booking creation breaks that: the booking itself is inserted by
// dbo.CreateBooking (§4.1 — the UPDLOCK/HOLDLOCK capacity check cannot be
// expressed in LINQ), while the ApprovalRequest and Notifications rows derived
// from it go through EF. Two mechanisms, one outcome — without a shared
// transaction, a failure between them leaves a confirmed booking whose
// notification never existed, or an approval request for a booking that was
// rejected.
//
// **Why it is a port and not a private helper.** WP-5's approval path is the
// same shape (dbo.ApproveBooking, then the decision row and its notifications),
// so the second caller is already scheduled.
//
// **Why the delegate shape, rather than Begin/Commit methods.** CLAUDE.md §5:
// EnableRetryOnFailure is on, so BeginTransaction cannot be called directly —
// the unit of work has to be wrapped in
// Database.CreateExecutionStrategy().ExecuteAsync(...) so a deadlock victim
// (1205) is retried as a whole. A Begin/Commit pair cannot express "run all of
// this again", and the retry is not optional here: the range lock in
// dbo.CreateBooking makes 1205 an expected outcome under contention, not an
// exceptional one.
//
// **The one thing a caller must get right.** The delegate can run more than
// once, so everything inside it has to be safe to repeat. In particular the
// booking's Guid is chosen *before* ExecuteAsync is called, never inside it, so
// a retry re-inserts the same row rather than minting a second booking.
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
