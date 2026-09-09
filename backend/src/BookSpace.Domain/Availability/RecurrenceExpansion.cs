using BookSpace.Domain.Entities;

namespace BookSpace.Domain.Availability;

// CLAUDE.md §4.3: recurrence expands in the application layer, in local
// wall-clock time, using the rule's own IANA TimeZoneId, then converts to
// UTC. This is that expansion — in Domain, alongside BookingEligibility,
// for the same reason: no EF, no I/O beyond the IResourceTimeZone port, and
// it is the one place a rule's occurrence dates and instants are computed,
// so a second implementation (the series-creation handler, say) would drift
// from it exactly the way AvailabilityCalculator's header warns about for the
// availability query and WP-4's rejections.
//
// Reuses RecurrenceRule.OccurrenceDate(index) — the same daily/weekly/monthly
// stepping ComputeImpliedEndDate already uses for the two-year span cap
// (decision 0007) — rather than re-deriving the arithmetic here. This class
// only decides, per date, what UTC instant (if any) that date's local times
// resolve to.
//
// Two DST rules apply, both at the level of a single instant rather than the
// range AvailabilityWindowExpansion answers for a window's boundaries —
// decision 0021's start-earlier/end-later split is a property of a *range*
// allowed to stretch to 25 hours, and does not transfer to an occurrence with
// a fixed nominal duration (see 0024's Context section):
//
//   - a local start or end that falls in a clocks-forward gap (decision 0008)
//     names no instant at all, so the whole occurrence is skipped rather than
//     resolved;
//   - an ambiguous, clocks-back local time (decision 0024, WP-5's first
//     decision) resolves to the *earlier* of its two candidate instants, for
//     both ends. IResourceTimeZone.ToUtcEarliest already implements this — it
//     only needs to be called, never ToUtcLatest, and never told the time was
//     ambiguous in the first place.
public static class RecurrenceExpansion
{
    public static IReadOnlyList<RecurrenceOccurrence> Expand(RecurrenceRule rule, IResourceTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(zone);

        var occurrences = new List<RecurrenceOccurrence>();

        // OccurrenceCount and EndDate are mutually exclusive
        // (CK_RecurrenceRules_EndCondition), so exactly one of the two checks
        // below ever fires to end the loop — the other's guard is simply never
        // true because its property is null.
        for (var index = 0; ; index++)
        {
            if (rule.OccurrenceCount is { } occurrenceCount && index >= occurrenceCount)
            {
                break;
            }

            var date = rule.OccurrenceDate(index);

            if (rule.EndDate is { } endDate && date > endDate)
            {
                break;
            }

            occurrences.Add(Resolve(rule, date, zone));
        }

        return occurrences;
    }

    private static RecurrenceOccurrence Resolve(RecurrenceRule rule, DateOnly date, IResourceTimeZone zone)
    {
        var localStart = date.ToDateTime(rule.LocalStartTime);
        var localEnd = date.ToDateTime(rule.LocalEndTime);

        // Both ends are tested, not just LocalStartTime — a clocks-forward gap
        // is at most a few hours (SystemResourceTimeZone bounds the walk at
        // four), so a start just before the gap and an end just inside it is a
        // real occurrence, not a hypothetical one, and it has just as little to
        // create a Booking from as one whose start falls in the gap.
        if (zone.IsInvalidLocalTime(localStart) || zone.IsInvalidLocalTime(localEnd))
        {
            return new RecurrenceOccurrence(date, RecurrenceOccurrenceOutcome.SkippedSpringForwardGap, null);
        }

        // 0024: earlier for both ends, deliberately not ToUtcLatest for the
        // end (that is 0021's rule for a range, and would silently lengthen
        // this occurrence by an hour on a fall-back date). ToUtcEarliest
        // already returns the single ordinary instant when the local time is
        // unambiguous, so nothing here needs to ask whether it was.
        var startUtc = zone.ToUtcEarliest(localStart);
        var endUtc = zone.ToUtcEarliest(localEnd);

        return new RecurrenceOccurrence(date, RecurrenceOccurrenceOutcome.Instant, new UtcInterval(startUtc, endUtc));
    }
}
