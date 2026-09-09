using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.RejectBooking;

// FR-7.1-7.5. POST /bookings/{id}/reject, on TenantMember — same reach as
// approve (decision 0018/0002 reapplied), per the owner's answer to
// wp5-plan.md's shape question 7.
//
// Note is optional, matching ApproveBookingCommandRequest — FR-7.2's
// "decision record with optional note", recorded on the ApprovalRequest.
public sealed record RejectBookingCommandRequest(Guid BookingId, string? Note = null)
    : IRequest<RejectBookingCommandResponse>;
