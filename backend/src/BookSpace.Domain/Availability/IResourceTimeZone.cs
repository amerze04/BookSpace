namespace BookSpace.Domain.Availability;

// One resource's timezone, already resolved, able to turn a resource-local wall
// clock into an instant. Availability is expressed in the resource's timezone
// (docs/decisions/0003-availability-timezone.md), so every expansion needs this
// and nothing else about the zone.
//
// Declared here rather than in Application/Abstractions with the other ports
// because the availability calculation itself lives in Domain — it is the
// read-side twin of FR-4.2's rejection rules, which WP-4 implements over the
// same data (docs/wp3-plan.md, "The architectural point"). A pure function
// cannot depend on an Application interface, and the alternative — Domain
// resolving zone ids itself — would put the host's timezone database behind
// code that is supposed to have no I/O. So Domain states the contract and
// ITimeZoneCatalog hands out implementations of it.
//
// Two methods rather than one because a wall-clock time does not always name
// exactly one instant, and which instant an interval wants depends on which
// end of the interval it is (WP-3 decision D3):
//
//   - a **missing** local time (the clocks-forward gap) names none, and both
//     methods answer with the transition instant — the moment the gap closes;
//   - an **ambiguous** local time (the clocks-back hour) names two, and an
//     interval takes the earlier one for its start and the later one for its
//     end, which is what makes the day 25 hours long instead of quietly
//     dropping an hour off the schedule.
//
// TimeZoneInfo's own defaults do neither: it throws on the first case and
// assumes standard time on the second, which is the later instant for both
// ends. Implementations must state the rule above instead of inheriting that.
public interface IResourceTimeZone
{
    // The earliest instant that can be called this local time — the start of an
    // interval.
    DateTime ToUtcEarliest(DateTime resourceLocal);

    // The latest instant that can be called this local time — the end of an
    // interval.
    DateTime ToUtcLatest(DateTime resourceLocal);

    // The resource-local wall clock at a given instant. Added in WP-4 Phase 1a
    // for BookingEligibility, which is handed a booking as UTC instants and has
    // to know which local dates to expand the weekly schedule over.
    //
    // One method, not a pair, and the asymmetry with the two above is real
    // rather than an oversight: this direction is always single-valued. Every
    // instant has exactly one offset in a zone, so it names exactly one wall
    // clock — it is only the reverse trip that can name none (the gap) or two
    // (the repeated hour). Nothing here needs an earliest/latest choice because
    // there is never anything to choose between.
    //
    // Returns DateTimeKind.Unspecified, which is what a wall clock is in .NET
    // and what the two methods above demand back (CLAUDE.md §4.3).
    DateTime ToLocal(DateTime utc);

    // Whether this resource-local wall-clock time never happened — true only
    // inside a clocks-forward gap. Added in WP-5 for RecurrenceExpansion:
    // decision 0008 skips a spring-forward occurrence rather than resolving it
    // to an instant, which is a different answer from what ToUtcEarliest gives
    // (the transition instant) and has to be asked for explicitly before that
    // method is called at all.
    //
    // False for an ordinary local time *and* for one inside a clocks-back
    // ambiguous hour — both of those already name a real instant, which
    // ToUtcEarliest/ToUtcLatest resolve without needing to be told anything.
    // Only the gap has nothing to resolve to.
    bool IsInvalidLocalTime(DateTime resourceLocal);
}
