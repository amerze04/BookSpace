using BookSpace.Domain.Availability;

namespace BookSpace.Infrastructure.Time;

// IResourceTimeZone against one resolved TimeZoneInfo, and the riskiest code in
// WP-3 Phase 5: it is where a resource-local wall clock becomes an instant, and
// TimeZoneInfo's own defaults are wrong for both of the cases that matter.
//
//   - ConvertTimeToUtc *throws* on a local time inside a clocks-forward gap.
//   - For an ambiguous local time in the clocks-back hour it assumes standard
//     time, which is the *later* of the two instants — for both ends of an
//     interval, quietly shortening a window by an hour on that one day.
//
// WP-3 decision D3 says a window absorbs the anomaly by expanding to the UTC
// interval that actually elapsed. Verified against real tzdata (2026-09-03):
// under the rules below, 2026-03-08 in America/New_York expands to 23 hours and
// 2026-11-01 to 25, with no policy choice anywhere.
//
// Distinct from a booking *occurrence*, which is an instant and has to land
// somewhere — that needed docs/decisions/0008 for the gap, and the clocks-back
// case for occurrences is still open (CLAUDE.md §9). Nothing here closes it.
internal sealed class SystemResourceTimeZone : IResourceTimeZone
{
    // A clocks-forward gap is one hour almost everywhere and two at most
    // (Antarctica/Troll). Four hours is a bound the walk below can never
    // legitimately reach, so hitting it means tzdata is shaped in a way this
    // code does not understand — worth failing loudly rather than spinning.
    private const int MaxGapHours = 4;

    private readonly TimeZoneInfo _zone;

    public SystemResourceTimeZone(TimeZoneInfo zone)
    {
        _zone = zone;
    }

    public DateTime ToUtcEarliest(DateTime resourceLocal) => Resolve(resourceLocal, earliest: true);

    public DateTime ToUtcLatest(DateTime resourceLocal) => Resolve(resourceLocal, earliest: false);

    private DateTime Resolve(DateTime resourceLocal, bool earliest)
    {
        // A wall clock is not an instant, and Unspecified is how .NET spells
        // that. Rejecting Utc and Local outright rather than letting
        // ConvertTimeToUtc do it: a UTC-Kind value arriving here means a caller
        // has already converted, and silently converting twice is the kind of
        // error CLAUDE.md §4.3's Kind conventions exist to prevent.
        if (resourceLocal.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "A resource-local wall-clock time must have DateTimeKind.Unspecified.",
                nameof(resourceLocal));
        }

        if (_zone.IsInvalidTime(resourceLocal))
        {
            return TransitionInstantAfterGap(resourceLocal);
        }

        if (_zone.IsAmbiguousTime(resourceLocal))
        {
            // Two offsets; UTC = local - offset, so the *larger* offset is the
            // *earlier* instant. An interval's start takes the earlier and its
            // end the later, which is what makes the day 25 hours long.
            var offsets = _zone.GetAmbiguousTimeOffsets(resourceLocal);
            var offset = earliest ? offsets.Max() : offsets.Min();

            return DateTime.SpecifyKind(resourceLocal - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(resourceLocal, _zone);
    }

    // The local times in a clocks-forward gap never happened, so there is one
    // sensible answer for both ends of an interval: the instant the gap closes.
    // In America/New_York on 2026-03-08 that maps every local time from 02:00:00
    // to 02:59:59 onto 07:00:00Z, which is 03:00 local.
    //
    // Found by walking forward to the first local time the zone considers real,
    // then converting that. Arithmetic on the offset would be shorter but needs
    // the gap's own start, which TimeZoneInfo does not expose without unpacking
    // AdjustmentRule.DaylightTransitionStart's floating "second Sunday in March"
    // form — more machinery than this is worth, for the same answer.
    //
    // One-second steps make the walk exact for anything this system can express:
    // OpensAt/ClosesAt are time(0) and every transition in tzdata falls on a
    // whole minute, so the first valid second is the transition itself. It runs
    // only for a local time actually inside a gap — twice a year at most per
    // resource — so a few thousand cheap checks in that case cost nothing.
    private DateTime TransitionInstantAfterGap(DateTime gapLocal)
    {
        var limit = gapLocal.AddHours(MaxGapHours);

        for (var probe = gapLocal; probe <= limit; probe = probe.AddSeconds(1))
        {
            if (!_zone.IsInvalidTime(probe))
            {
                return TimeZoneInfo.ConvertTimeToUtc(probe, _zone);
            }
        }

        throw new InvalidOperationException(
            $"No valid local time found within {MaxGapHours} hours of {gapLocal:O} in " +
            $"time zone '{_zone.Id}'.");
    }
}
