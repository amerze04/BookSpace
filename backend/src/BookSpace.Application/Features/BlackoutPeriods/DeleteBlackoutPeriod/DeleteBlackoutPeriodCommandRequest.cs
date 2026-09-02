using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.DeleteBlackoutPeriod;

// FR-3.4, TenantAdmin only.
// DELETE /resources/{resourceId}/blackout-periods/{id}.
//
// **A real hard delete, and the first in this system** — owner's call,
// 2026-09-02. CLAUDE.md §4.5 ("nothing is deleted") is scoped to users and
// resources, which are archived because their booking history has to stay
// readable (FR-3.5). Nothing hangs history off a blackout: the reason a booking
// was cancelled is a *text snapshot* on the booking itself
// (Bookings.CancellationReason, see BlackoutCascade), not a foreign key, so the
// audit trail survives the row's removal intact.
//
// It does **not** un-cancel anything. Deleting a blackout means "stop blocking
// future bookings from this point on", never "undo" — Booking has no Uncancel,
// and inventing one would mean a booking's owner is emailed that their meeting
// is off and then finds it silently back on their calendar. Recorded in
// docs/decisions/0019-blackout-period-lifecycle.md, because it is the one thing
// about this endpoint a client could reasonably assume the other way.
//
// IRequest<Unit>: the mediator has no void overload, and the endpoint returns
// 204 with no body — there is nothing useful to say about a row that no longer
// exists.
public sealed record DeleteBlackoutPeriodCommandRequest(
    Guid ResourceId,
    Guid BlackoutPeriodId) : IRequest<Unit>;
