using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.CheckIn;

// The 200 body of POST /bookings/{id}/check-in. Per-endpoint and in its own
// file per decision 0015's amendment.
//
// CheckedInAtUtc is non-nullable here: a response only exists once
// Booking.CheckIn has run (fresh or, on a repeat call, already holding), so
// it is always set by the time this is built.
public sealed record CheckInCommandResponse(
    Guid Id,
    Guid ResourceId,
    Guid UserId,
    BookingStatus Status,
    DateTime CheckedInAtUtc);
