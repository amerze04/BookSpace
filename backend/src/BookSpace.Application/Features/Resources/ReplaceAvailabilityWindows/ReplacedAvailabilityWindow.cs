namespace BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

// One window as it now exists, in the PUT response. Carries the Id the request
// item does not, because this is the first point at which the row has one.
//
// Its own type rather than the read detail's AvailabilityWindowDetail
// (convention agreed 2026-09-01, docs/decisions/0015): the two are identical
// today and are free to stop being. The read detail is the likelier to grow —
// Phase 5's availability query may well want a window to report whether it is
// currently in effect — and that field has no business appearing in the echo of
// a write.
public sealed record ReplacedAvailabilityWindow(
    Guid Id,
    DayOfWeek Weekday,
    TimeOnly OpensAt,
    TimeOnly ClosesAt);
