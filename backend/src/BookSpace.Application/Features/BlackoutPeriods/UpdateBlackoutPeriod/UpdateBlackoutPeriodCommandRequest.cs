using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;

// FR-3.4, TenantAdmin only. PUT /resources/{resourceId}/blackout-periods/{id}.
//
// A full representation, not a patch (docs/decisions/0015): every mutable field
// is supplied and an omitted Reason means cleared. Same fields as the create
// command — both ids travel in the route instead of the body, so they cannot
// disagree with it.
//
// **Re-runs decision 0001's cascade over the new interval**, which is the whole
// reason this is not ordinary CRUD. 0001 names the case explicitly ("created, or
// edited to a wider range"), and widening is not the only direction that
// matters: moving a blackout from Tuesday to Wednesday blacks out a set of
// bookings that were never touched by the original.
//
// Narrowing or moving does **not** restore anything. A cancellation is
// irreversible — Booking has no Uncancel, and inventing one would mean a
// booking's owner learns by email that their meeting is off and then finds it
// silently back on the calendar. So the cascade is applied forwards only: the
// new interval cancels what it overlaps, and what the old interval already
// cancelled stays cancelled. Owner's call, 2026-09-02; recorded in
// docs/decisions/0019-blackout-period-lifecycle.md.
public sealed record UpdateBlackoutPeriodCommandRequest(
    Guid ResourceId,
    Guid BlackoutPeriodId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason)
    : IRequest<UpdateBlackoutPeriodCommandResponse>, IBlackoutPeriodWriteCommand;
