using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.GetBooking;

// The approval half of a booking's detail read (FR-7.1-7.4, WP-5 Phase 3),
// present only when the resource required one and null otherwise.
//
// Its own type rather than CreateBooking's BookingApprovalDetail (decision
// 0015: per-endpoint DTOs, never shared, even where fields coincide) — and here
// they do not stay coincident for long. A freshly created Pending booking's
// approval is always Pending by construction, so that response needs only
// ApprovalRequestId and ExpiresAtUtc; this one is read any time after, so it
// also carries the decision once made — Decision, DecidedByUserId, DecidedAtUtc
// and the approver's Note. RequestedAtUtc joins it too, since a detail read is
// where "how long has this been waiting" is actually asked.
public sealed record GetBookingApprovalDetail(
    Guid ApprovalRequestId,
    DateTime RequestedAtUtc,
    DateTime? ExpiresAtUtc,
    ApprovalDecision Decision,
    Guid? DecidedByUserId,
    DateTime? DecidedAtUtc,
    string? Note);
