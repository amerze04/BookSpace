using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.CreateBooking;

// POST /bookings, 201. Per-endpoint and in its own file, per decision 0015's
// amendment — the read endpoints in Phase 2 declare their own even where the
// fields coincide.
//
// **Status is the field that matters most here**, and it is why this is not just
// an echo of the request: a member who booked an approval-gated resource gets
// Pending, not Confirmed, and needs to be told so at the moment of booking
// rather than discovering it in a list later (FR-7.1). It serializes as its name
// — "Pending", not 0 — because Program.cs registered JsonStringEnumConverter
// app-wide in WP-3 Phase 3.
//
// RemainingCapacity is deliberately absent even though dbo.CreateBooking
// computes it: it is true only for the instant it was measured, and putting a
// number a client might act on into a create response invites exactly the
// check-then-act race the procedure exists to prevent. The availability endpoint
// is where that question belongs.
public sealed record CreateBookingCommandResponse(
    Guid Id,
    Guid ResourceId,
    Guid UserId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title,
    BookingStatus Status,
    DateTime CreatedAtUtc,
    BookingApprovalDetail? Approval);
