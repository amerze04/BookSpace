using BookSpace.Domain.Entities;

namespace BookSpace.Domain.Availability;

// Step one of the availability calculation (WP-3 Phase 5): a resource's weekly
// schedule, stored as resource-local wall-clock time (FR-3.2,
// docs/decisions/0003-availability-timezone.md), becomes the actual instants it
// opens for across a range of resource-local dates.
//
// Local dates in, not UTC instants: the range a client asks about is expressed
// in the resource's own calendar (owner's call, 2026-09-03), because "Tuesday
// the 8th" is what a member picks and a pair of instants would force the server
// to work out which local days they touch anyway.
//
// The returned intervals are merged, ordered and non-overlapping — the shape
// the blackout subtraction and the capacity sweep in IntervalAlgebra expect.
public static class AvailabilityWindowExpansion
{
    // The largest value a time(0) column can hold, and this system's spelling of
    // "closes at midnight".
    //
    // AvailabilityWindow cannot say 24:00: CK_AvailabilityWindows_Window
    // requires ClosesAt > OpensAt, so a resource open overnight is stored as two
    // rows on consecutive weekdays, and the later one has to close at the last
    // instant of its own day. Taken literally that leaves a one-second hole at
    // every midnight boundary, which is invisible at booking granularity but
    // would still be a hole no admin asked for — an admin who writes 23:59:59
    // means "until midnight", because it is the only way to say it.
    //
    // Promoting it to the next day's midnight is therefore the more faithful
    // reading, and it is also what turns the overnight join into an ordinary
    // merge: the promoted end and the next day's 00:00 opening become the same
    // instant, so IntervalAlgebra.Merge joins them with no special case.
    public static readonly TimeOnly ClosesAtEndOfDay = new(23, 59, 59);

    public static IReadOnlyList<UtcInterval> ExpandToUtc(
        IEnumerable<AvailabilityWindow> windows,
        DateOnly fromLocalDate,
        DateOnly toLocalDate,
        IResourceTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(zone);

        if (toLocalDate < fromLocalDate)
        {
            throw new ArgumentException(
                "toLocalDate must not be earlier than fromLocalDate.",
                nameof(toLocalDate));
        }

        // Grouped once rather than filtered per date: a 90-day range would
        // otherwise re-scan the whole schedule 90 times. The range cap itself is
        // the endpoint validator's business, not this function's.
        var byWeekday = windows.GroupBy(w => w.Weekday).ToDictionary(g => g.Key, g => g.ToList());
        var expanded = new List<UtcInterval>();

        for (var date = fromLocalDate; date <= toLocalDate; date = date.AddDays(1))
        {
            if (!byWeekday.TryGetValue(date.DayOfWeek, out var dayWindows))
            {
                continue;
            }

            foreach (var window in dayWindows)
            {
                var opensLocal = date.ToDateTime(window.OpensAt);
                var closesLocal = window.ClosesAt == ClosesAtEndOfDay
                    ? date.AddDays(1).ToDateTime(TimeOnly.MinValue)
                    : date.ToDateTime(window.ClosesAt);

                // Earliest for the opening and latest for the closing: on the
                // day the clocks go back this is what gives the window the 25
                // hours it really lasts, instead of silently losing the repeated
                // hour (WP-3 decision D3, and see IResourceTimeZone).
                var startUtc = zone.ToUtcEarliest(opensLocal);
                var endUtc = zone.ToUtcLatest(closesLocal);

                // A window lying entirely inside a clocks-forward gap opens and
                // closes at the same instant — the transition — so it is not a
                // span at all and contributes nothing. That is D3 working as
                // intended rather than an error: those local times did not
                // happen that day. Dropped here because UtcInterval refuses to
                // represent an empty span.
                if (endUtc > startUtc)
                {
                    expanded.Add(new UtcInterval(startUtc, endUtc));
                }
            }
        }

        return IntervalAlgebra.Merge(expanded);
    }
}
