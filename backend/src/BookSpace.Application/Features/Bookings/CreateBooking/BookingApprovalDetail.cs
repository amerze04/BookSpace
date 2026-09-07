namespace BookSpace.Application.Features.Bookings.CreateBooking;

// The approval half of a create response, present only when the resource
// required one (FR-7.1) and null otherwise.
//
// Its own record rather than two nullable fields on the response, because the
// two travel together: a booking either has a decision pending against it or it
// does not, and a null ApprovalRequestId beside a non-null ExpiresAtUtc would be
// a state the system cannot produce but the contract would allow.
//
// ExpiresAtUtc is nullable inside it for a different reason — a tenant that has
// set no Organizations.ApprovalExpiryHours leaves requests pending indefinitely
// (FR-7.4), which is a legitimate configuration rather than a missing value.
//
// Approvers are not listed here. GET /resources/{id} already returns them for
// any member (WP-3 Phase 3), so repeating them on every create would duplicate a
// contract that is allowed to grow admin-only fields later.
public sealed record BookingApprovalDetail(Guid ApprovalRequestId, DateTime? ExpiresAtUtc);
