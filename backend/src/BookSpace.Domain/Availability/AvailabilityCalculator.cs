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
    // The number of units a booker wants to hold at once when the endpoint is not
    // asked. One is the honest default: it is the smallest legal booking
    // (CK_Bookings_Quantity), so it yields the most permissive — and therefore
    // most complete — answer, and on an exclusive resource (Capacity 1) it is the
    // only possible value.
    public const int DefaultRequiredQuantity = 1;

    public static IReadOnlyList<BookableInterval> BookableIntervals(
        Resource resource,
        DateOnly fromLocalDate,
        DateOnly toLocalDate,
        IResourceTimeZone zone,
        IEnumerable<UtcInterval> blackouts,
        IEnumerable<BookedQuantity> bookings,
        int requiredQuantity = DefaultRequiredQuantity)
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

        // 3. Existing bookings consume units, not time. What comes back is the
        //    runs where at least requiredQuantity units are free throughout,
        //    each carrying the floor of what is left across it (decision 0020).
        var bookable = CapacitySweep.Subtract(
            afterBlackouts, bookings, resource.Capacity, requiredQuantity);

        // 4. An endpoint promising bookable time should not offer a span too
        //    short to book (owner's call, 2026-09-03).
        return DropShorterThanMinimumDuration(resource, bookable);
    }

    // Correct as written *because* step 3 already rejoined every run that can
    // hold the booking. That was not true before 2026-09-04: the sweep used to
    // cut wherever the remaining figure changed, so this filter measured
    // constant-capacity fragments rather than bookable runs, and a single 1-unit
    // booking in the middle of an open day could push the spans either side of it
    // under the floor and delete them from the answer — time WP-4 would have
    // accepted a booking for. Asking for a quantity is what made the runs
    // well-defined, and measuring the runs is what makes this filter honest.
    //
    // The rule itself asks the *resource*, not this method: MinDurationMinutes is
    // read through Resource.CanFitABooking so that WP-4's booking-length
    // rejection and this filter cannot drift apart. Only the minimum bears on a
    // span — see that method for why the maximum does not.
    private static IReadOnlyList<BookableInterval> DropShorterThanMinimumDuration(
        Resource resource,
        IReadOnlyList<BookableInterval> intervals)
    {
        if (resource.MinDurationMinutes is null)
        {
            return intervals;
        }

        return intervals.Where(i => resource.CanFitABooking(i.Interval.Duration)).ToList();
    }
}
