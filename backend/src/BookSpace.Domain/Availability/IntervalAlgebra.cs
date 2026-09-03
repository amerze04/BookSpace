namespace BookSpace.Domain.Availability;

// Pure set operations over UtcInterval, and the home of the availability
// calculation's arithmetic (WP-3 Phase 5). No EF, no I/O, no clock — inputs in,
// intervals out, which is what CLAUDE.md §3 reserves Domain for.
//
// It lives here rather than beside the Application-layer …Rules classes of
// Phases 2-4 because those are validation and this is the domain's core
// algorithm, with a second consumer already scheduled: WP-4's booking
// rejections (OutsideAvailability, BlackoutPeriod, CapacityExceeded) are the
// same question asked of one interval instead of a range. Two implementations
// of that would drift, and the failure mode is the API offering a slot it then
// refuses to book.
//
// Every operation returns intervals ordered by start and non-overlapping, so
// the next operation can rely on that shape without re-normalising. Blackout
// subtraction and the capacity sweep join this class in step 2.
public static class IntervalAlgebra
{
    // Collapses a set of intervals into the smallest ordered, non-overlapping
    // set covering the same instants. Overlapping *and* merely touching
    // intervals are joined — touching is the common case here, not a corner:
    //
    //   - Phase 3 deliberately allows adjacent windows on one weekday
    //     (09:00-12:00 + 12:00-17:00, because ClosesAt is exclusive), and a
    //     member asking what is bookable should see one span, not two;
    //   - a window that runs to the end of the day and one that opens at
    //     midnight the next day are one overnight span, which is the only way
    //     this schema can express a resource that never closes
    //     (CK_AvailabilityWindows_Window requires ClosesAt > OpensAt, so
    //     22:00-02:00 has to be stored as two rows).
    //
    // Sorting first is what makes one pass sufficient: once the intervals are
    // ordered by start, an interval can only ever extend the last one kept, and
    // never reach back past it.
    public static IReadOnlyList<UtcInterval> Merge(IEnumerable<UtcInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var ordered = intervals.OrderBy(i => i.StartUtc).ThenBy(i => i.EndUtc).ToList();
        var merged = new List<UtcInterval>(ordered.Count);

        foreach (var interval in ordered)
        {
            if (merged.Count == 0 || interval.StartUtc > merged[^1].EndUtc)
            {
                merged.Add(interval);
                continue;
            }

            // Overlapping or touching. Extend only if this one actually reaches
            // further — a short interval nested inside the last one changes
            // nothing.
            if (interval.EndUtc > merged[^1].EndUtc)
            {
                merged[^1] = new UtcInterval(merged[^1].StartUtc, interval.EndUtc);
            }
        }

        return merged;
    }

    // Interval difference: everything in `from` that is not covered by `remove`.
    // Ordered and non-overlapping, like Merge, so the capacity sweep after it can
    // rely on the shape.
    //
    // This is how a blackout period overrides the weekly schedule (FR-3.4). A
    // blackout does not shorten a window or cancel a day — it removes the
    // instants it covers, which may take a chunk out of the middle of an open
    // span and leave two, or swallow it whole and leave none.
    //
    // Both sides are merged first, which is what makes one ordered pass enough
    // and also the reason overlapping blackouts need no special handling: they
    // are explicitly allowed on one resource
    // (docs/decisions/0019-blackout-period-lifecycle.md, because the union of two
    // blackouts is still blacked out), and merging turns any such pile into the
    // single span it means.
    public static IReadOnlyList<UtcInterval> Subtract(
        IEnumerable<UtcInterval> from,
        IEnumerable<UtcInterval> remove)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(remove);

        var source = Merge(from);
        var cuts = Merge(remove);

        if (cuts.Count == 0)
        {
            return source;
        }

        var remaining = new List<UtcInterval>(source.Count);

        foreach (var interval in source)
        {
            // Walks left to right through the cuts, emitting whatever survives
            // in front of each one. `openFrom` is the earliest instant of this
            // interval not yet accounted for.
            var openFrom = interval.StartUtc;

            foreach (var cut in cuts)
            {
                if (cut.EndUtc <= openFrom)
                {
                    continue; // Entirely behind us.
                }

                if (cut.StartUtc >= interval.EndUtc)
                {
                    break; // Cuts are ordered, so every later one is past this interval.
                }

                if (cut.StartUtc > openFrom)
                {
                    remaining.Add(new UtcInterval(openFrom, cut.StartUtc));
                }

                openFrom = cut.EndUtc;

                if (openFrom >= interval.EndUtc)
                {
                    break; // Nothing of this interval is left.
                }
            }

            if (openFrom < interval.EndUtc)
            {
                remaining.Add(new UtcInterval(openFrom, interval.EndUtc));
            }
        }

        return remaining;
    }
}
