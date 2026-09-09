using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.ApproveBooking;

// The 200 body of POST /bookings/{id}/approve. Per-endpoint and in its own
// file per decision 0015's amendment, even though it is nearly identical to
// RejectBookingCommandResponse — the two are different decisions with
// different futures (an approved booking's DTO is the one that would grow an
// approval-detail field first, if one were ever added).
public sealed record ApproveBookingCommandResponse(
    Guid Id,
    BookingStatus Status,
    Guid DecidedByUserId,
    DateTime DecidedAtUtc);
