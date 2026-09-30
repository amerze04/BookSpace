using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.CheckIn;

// WP-8 Phase 3 (docs/wp8-plan.md, decision D5). POST /bookings/{id}/check-in,
// on TenantMember. No body: check-in carries no reason or note, only who and
// when, and both come from the token and the clock rather than the request.
public sealed record CheckInCommandRequest(Guid BookingId) : IRequest<CheckInCommandResponse>;
