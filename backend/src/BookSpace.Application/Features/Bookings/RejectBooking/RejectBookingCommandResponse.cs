using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.RejectBooking;

// The 200 body of POST /bookings/{id}/reject. Per-endpoint and in its own
// file per decision 0015's amendment — see ApproveBookingCommandResponse's
// header for why it is not shared with the near-identical approve response.
public sealed record RejectBookingCommandResponse(
    Guid Id,
    BookingStatus Status,
    Guid DecidedByUserId,
    DateTime DecidedAtUtc);
