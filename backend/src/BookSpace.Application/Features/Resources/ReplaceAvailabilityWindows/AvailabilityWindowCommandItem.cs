namespace BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

// One window in the incoming weekly schedule (FR-3.2). No Id: the whole set is
// replaced, so the server mints ids for the rows it writes and a client has
// nothing to correlate them to — sending one would only invite the belief that
// editing a single window in place is possible.
//
// No ResourceId either; it travels in the route, so the payload cannot disagree
// with the URL.
//
// Times are resource-local wall clock, not UTC
// (docs/decisions/0003-availability-timezone.md). A window is a recurring weekly
// rule rather than an instant, so it has no offset to carry — which is also why
// changing a resource's TimeZoneId reinterprets every one of these
// (UpdateResource's TimeZoneChangeNotice) instead of rewriting them.
public sealed record AvailabilityWindowCommandItem(
    DayOfWeek Weekday,
    TimeOnly OpensAt,
    TimeOnly ClosesAt);
