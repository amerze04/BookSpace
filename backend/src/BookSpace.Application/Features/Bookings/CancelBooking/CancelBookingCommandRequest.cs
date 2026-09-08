using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.CancelBooking;

// FR-4.4 / decision 0002. POST /bookings/{id}/cancel, on TenantMember.
//
// **A member cancels their own; a TenantAdmin cancels any booking in their
// tenant.** That is decision 0002, taken 2026-08-19 and implemented here rather
// than re-decided — the work package listed it as an open question and it was
// already answered. `Bookings.CancelledByUserId` records the actor distinctly
// from `Bookings.UserId`, so an admin cancelling someone else's meeting is
// visible on the row afterwards rather than being indistinguishable from the
// owner doing it.
//
// **POST .../cancel, not DELETE /bookings/{id}.** CLAUDE.md §4.5 deletes
// nothing, and a cancellation records an actor, a time and a reason — a DELETE
// that quietly meant "cancel, keeping the row" would misdescribe itself. Same
// argument that made archive a POST in WP-3.
//
// **Deliberately not idempotent**, and this is where it differs from archive: a
// second cancellation would overwrite CancelledByUserId, CancelledAtUtc and the
// reason with a second actor's, so the record of who called the meeting off
// would quietly change. Archive has nothing to overwrite, which is why that one
// can safely no-op and this one returns BookingNotCancellable (422).
//
// Reason is optional — "the meeting is off" is often all there is to say — and
// capped at the column's own 300 characters rather than truncated, matching how
// an oversized pageSize is rejected (decision 0015).
public sealed record CancelBookingCommandRequest(Guid BookingId, string? Reason = null)
    : IRequest<CancelBookingCommandResponse>;
