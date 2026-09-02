namespace BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;

// One row of GET /resources/{id}/blackout-periods.
//
// Its own type rather than one shared with the create response
// (docs/decisions/0015), and unusually the two differ in substance rather than
// only on principle: the create response carries the bookings the cascade
// cancelled, which is a fact about one moment and not a property of the row.
//
// ResourceId is echoed on every row even though it is fixed by the route, so a
// row pasted into a log or a test fixture still says what it belongs to — the
// same reason the Phase 3 write responses echo it.
public sealed record ListBlackoutPeriodsQueryResponse(
    Guid Id,
    Guid ResourceId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason,
    DateTime CreatedAtUtc);
