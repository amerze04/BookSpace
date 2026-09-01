namespace BookSpace.Application.Features.Resources.GetResource;

// One window on the read detail (FR-3.2). Ordered by weekday then opening time
// by the repository, so a client can render the week without sorting.
//
// Its own type rather than ReplaceAvailabilityWindows' ReplacedAvailabilityWindow
// (convention agreed 2026-09-01, docs/decisions/0015). Identical today; this is
// the one free to grow, since Phase 5's availability query is a read concern and
// anything it wants reported per window belongs here rather than in the echo of
// a write.
//
// Times are resource-local wall clock, not UTC
// (docs/decisions/0003-availability-timezone.md) — read them against the
// enclosing response's TimeZoneId.
public sealed record AvailabilityWindowDetail(
    Guid Id,
    DayOfWeek Weekday,
    TimeOnly OpensAt,
    TimeOnly ClosesAt);
