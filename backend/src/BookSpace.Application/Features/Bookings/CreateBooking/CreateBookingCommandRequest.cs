using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.CreateBooking;

// FR-4.1: a member creates a one-off booking for an available resource and time
// range. POST /bookings, on TenantMember.
//
// **Instants, not local wall clock.** The availability endpoint already answers
// in UTC instants, so a client picks an interval out of that response and echoes
// its bounds back. That is also why WP-4 cannot resolve §9's open DST fall-back
// question: a one-off booking never names an ambiguous local time, because it
// never names a local time at all. Recurrence does, and that is WP-5.
//
// **Not nested under /resources/{id}.** A booking is its own aggregate and a
// member's list of them spans resources, so the resource travels in the body
// here rather than in the route — unlike blackout periods, which have no meaning
// apart from the resource they block.
//
// No OrgId and no UserId: both come from the token, via ICurrentTenant and
// ICurrentUser, so neither can be forged by editing a body. A member books for
// themselves; booking on someone else's behalf is not in the PRD.
//
// Quantity defaults to 1 at the controller — the smallest legal booking
// (CK_Bookings_Quantity), the same default the availability query takes, and the
// only value an exclusive resource can accept (decision 0005).
public sealed record CreateBookingCommandRequest(
    Guid ResourceId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title)
    : IRequest<CreateBookingCommandResponse>;
