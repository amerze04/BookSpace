using BookSpace.Domain.Entities;

namespace BookSpace.Domain.Availability;

// The whole availability calculation, as one call over plain inputs. WP-3
// Phase 5.
//
// Everything below it — expansion, merging, blackout subtraction, the capacity
// sweep — is separately testable and separately readable, but this is the entry
// point, and having exactly one matters more than it looks. WP-4 has to answer
// "may this booking be created" over the same four inputs
// (OutsideAvailability, BlackoutPeriod, CapacityExceeded), and if it composes
// the steps itself the two orderings will drift. The failure mode is the one
// docs/wp3-plan.md names: the API offers a member a slot and then refuses the
// booking for it.
//
// No EF, no clock, no I/O — the caller has already done the loading, and it is
// the caller that knows which bookings count (Pending and Confirmed) and how to
// resolve the resource's zone id.
//
// Two things deliberately *not* decided here:
//
//   - **IsArchived.** An archived resource returns an empty list from the
//     endpoint (owner's call, 2026-09-03), but that is the endpoint's answer,
//     not the schedule's: FR-3.5 keeps an archived resource readable, and its
//     windows still describe when it used to open. WP-4 needs a different answer
//     again — a ResourceArchived rejection, not silence — so collapsing both
//     into "empty" here would take the choice away from both callers.
//   - **The length of the range.** The 90-day cap is a request-shape rule and
//     belongs to the endpoint's validator, which can report it as a field error.
public static class AvailabilityCalculator
{
    public static IReadOnlyList<BookableInterval> BookableIntervals(
        Resource resource,
        DateOnly fromLocalDate,
        DateOnly toLocalDate,
        IResourceTimeZone zone,
        IEnumerable<UtcInterval> blackouts,
        IEnumerable<BookedQuantity> bookings)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(blackouts);
        ArgumentNullException.ThrowIfNull(bookings);

        // 1. When does the weekly schedule say this resource is open, as real
        //    instants? (Local wall clock in, UTC out — see IResourceTimeZone.)
        var open = AvailabilityWindowExpansion.ExpandToUtc(
            resource.AvailabilityWindows, fromLocalDate, toLocalDate, zone);

        // 2. Blackouts override availability (FR-3.4): remove the time they
        //    cover, which can split an open span in two or delete it entirely.
        var afterBlackouts = IntervalAlgebra.Subtract(open, blackouts);

        // 3. Existing bookings consume units, not time, so what comes back is
        //    intervals carrying what is left of Capacity (decision D2).
        var bookable = CapacitySweep.Subtract(afterBlackouts, bookings, resource.Capacity);

        // 4. An endpoint promising bookable time should not offer a span too
        //    short to book (owner's call, 2026-09-03).
        return DropShorterThanMinimumDuration(bookable, resource.MinDurationMinutes);
    }

    // Applied to the intervals as they come out of the sweep, which is the
    // owner's stated rule and also has a consequence worth knowing about: the
    // sweep cuts wherever remaining capacity changes, so a booking of one unit
    // in the middle of a long open span leaves three shorter intervals, and the
    // two flanking it can fall under the floor and disappear — even though a
    // single-unit booking spanning the whole run would in fact be accepted.
    //
    // That is inherent in decision D2's response shape: an interval carries one
    // remaining-capacity figure, so it cannot also express "at least one unit,
    // for longer". Flagged rather than worked around, because widening it is a
    // contract question, not an implementation detail.
    private static IReadOnlyList<BookableInterval> DropShorterThanMinimumDuration(
        IReadOnlyList<BookableInterval> intervals,
        int? minDurationMinutes)
    {
        if (minDurationMinutes is null)
        {
            return intervals;
        }

        var minimum = TimeSpan.FromMinutes(minDurationMinutes.Value);

        return intervals.Where(i => i.Interval.Duration >= minimum).ToList();
    }
}
