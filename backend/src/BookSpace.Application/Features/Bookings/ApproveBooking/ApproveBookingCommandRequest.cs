using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.ApproveBooking;

// FR-7.1-7.5, AC-5. POST /bookings/{id}/approve, on TenantMember — the
// approval reach (decision 0018/0002 reapplied) is a handler-level question,
// not a policy on the route, exactly as decision 0002's cancellation reach
// already is: the same route would otherwise need one URL per role.
//
// Note is optional, matching CancelBookingCommandRequest's Reason — FR-7.2's
// "decision record with optional note", recorded on the ApprovalRequest
// rather than the Booking.
public sealed record ApproveBookingCommandRequest(Guid BookingId, string? Note = null)
    : IRequest<ApproveBookingCommandResponse>;
