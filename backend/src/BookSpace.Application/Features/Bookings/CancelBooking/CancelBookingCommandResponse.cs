using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.CancelBooking;

// The 200 body of POST /bookings/{id}/cancel. Per-endpoint and in its own file
// per decision 0015's amendment.
//
// A body rather than a 204, matching archive and for the same reason: the useful
// reply is the row with the transition applied, so a client sees Status flip to
// Cancelled without a second request. Here it carries more than archive's,
// because a cancellation records *who* and *when*: CancelledByUserId differing
// from UserId is exactly how a member learns an admin called their meeting off
// (decision 0002), and it is information the create response could never have.
//
// **The interval is on the wire even though the booking no longer holds it.**
// The slot it freed is the point of the reply — a client updating a calendar
// needs to know which span just became bookable again, and re-reading the
// availability endpoint would answer a different question (what is free *now*,
// not what this action released).
//
// CancelledAtUtc and CancelledByUserId are non-nullable here, unlike on the read
// detail: on a response to a cancellation both are always set. The blackout
// cascade's null actor (Booking.CancelForBlackout) never reaches this endpoint,
// which only ever runs with a real person behind it.
public sealed record CancelBookingCommandResponse(
    Guid Id,
    Guid ResourceId,
    Guid UserId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title,
    BookingStatus Status,
    Guid CancelledByUserId,
    DateTime CancelledAtUtc,
    string? CancellationReason);
