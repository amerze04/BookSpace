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
}
