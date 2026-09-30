using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// WP-8 Phase 3 (docs/wp8-plan.md, decision 0004, FR-9.1). The no-show
// release job's port: sweep every Confirmed, not-yet-checked-in booking
// whose *own organisation's* grace period has elapsed, release it, and
// queue the notification telling its owner the slot is bookable again.
//
// A repository of its own rather than a method on IBookingRepository,
// matching INotificationRepository's own shape (docs/wp8-plan.md decision
// D9): nothing here is a per-request, per-tenant operation — one sweep tick
// evaluates bookings across every organisation at once, which
// IBookingRepository's own header frames itself around never doing.
public interface INoShowReleaseRepository
{
    // Decision 0004's predicate (Booking.IsNoShow), evaluated per booking
    // against its own organisation's NoShowGraceMinutes — a tenant setting,
    // not a global constant. Tracked, so the job mutates each one through
    // Booking.MarkNoShow via TryReleaseAsync below; bounded and ordered by
    // StartsAtUtc so the batch is deterministic, the same reasoning
    // FindOccurrencesToCancelAsync gives for its own ordering.
    Task<IReadOnlyList<Booking>> FindNoShowCandidatesAsync(
        int batchSize, DateTime nowUtc, CancellationToken cancellationToken);

    // Releases one booking and queues its NoShowReleased notification in a
    // single save, isolated from every other candidate in the same batch.
    // False means a concurrent check-in on *this* booking moved its
    // RowVersion since FindNoShowCandidatesAsync's read — not a failure, the
    // release job exists precisely to detect the *absence* of a check-in,
    // and this one no longer qualifies. True means it was released just now.
    Task<bool> TryReleaseAsync(Booking booking, DateTime nowUtc, CancellationToken cancellationToken);
}
