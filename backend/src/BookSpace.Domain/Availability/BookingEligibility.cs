using BookSpace.Domain.Entities;

namespace BookSpace.Domain.Availability;

// "May this exact interval be booked, and if not, why?" — FR-4.3's three
// rejections, answered over the same four inputs AvailabilityCalculator uses
// (WP-4 Phase 1a).
//
// **This is the reason the availability calculation lives in Domain.**
// AvailabilityCalculator's header names this class before it existed: WP-4 has
// to ask the same question of one interval that WP-3 asks of a range, and a
// second implementation would drift, with the failure mode being an API that
// offers a member a slot and then refuses to book it. So this composes the very
// same primitives — AvailabilityWindowExpansion, IntervalAlgebra.Subtract,
// CapacitySweep.Subtract — in the very same order, and adds only the
// containment test and the ordering of the reasons.
//
// Why it cannot simply call AvailabilityCalculator.BookableIntervals and check
// whether the request falls inside one: that function deliberately collapses
// every cause into a *gap* (decision 0020 — a gap says "nothing bookable here"
// without saying whether that is a blackout, a wall or a closing time). A
// booker being refused has to be told which, so this asks the questions
// separately and keeps the answer.
//
// No EF, no clock, no I/O. Three things are deliberately **not** decided here,
// all for the same reason AvailabilityCalculator leaves them out — the caller
// has information this function does not:
//
//   - **IsArchived.** FR-3.5 makes an archived resource readable but unbookable,
//     which is a ResourceArchived refusal rather than any of these four. The
//     handler checks it first.
//   - **The duration limits.** Resource.AllowsBookingDuration is the one place
//     that rule lives (added 2026-09-04 precisely so WP-4 would not re-derive
//     it), and it is a question about the request alone, not about what else is
//     on the calendar.
//   - **Whether the interval is in the past.** That needs a clock, and nothing
//     in this namespace has one.
public static class BookingEligibility
{
    // The reasons are applied in a fixed order — schedule, then blackouts, then
    // capacity — and the order is part of the contract, not an implementation
    // detail. Two properties come out of it:
    //
    //   - **It is stable.** An interval that is both outside the schedule and
    //     inside a blackout always reports OutsideAvailability, so a client's
    //     handling of a given request never changes for reasons it cannot see.
    //   - **It is the cheapest-first order**, and also the most structural
    //     first: "the resource is never open then" is a more useful thing to
    //     tell a booker than "someone has it", and it stays true tomorrow.
    //
    // It is also the order the calculation has to run in anyway. Blackouts are
    // subtracted before bookings because a booking inside blacked-out time was
    // already cancelled by decision 0001's cascade, so counting its units would
    // be counting a claim that no longer exists.
    public static BookingEligibilityResult Evaluate(
        Resource resource,
        UtcInterval requested,
        int quantity,
        IResourceTimeZone zone,
        IEnumerable<UtcInterval> blackouts,
        IEnumerable<BookedQuantity> bookings)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(blackouts);
        ArgumentNullException.ThrowIfNull(bookings);

        if (quantity <= 0) // CK_Bookings_Quantity
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        // The schedule is stored as resource-local wall clock (decision 0003)
        // and the request arrives as instants, so the local dates it touches
        // have to be worked out before anything can be expanded. Both ends are
        // converted: a booking can cross local midnight, and in a zone far from
        // UTC the local date is routinely not the UTC one.
        var fromLocalDate = DateOnly.FromDateTime(zone.ToLocal(requested.StartUtc));
        var toLocalDate = DateOnly.FromDateTime(zone.ToLocal(requested.EndUtc));

        // Merged, ordered and non-overlapping, and — because Merge joins
        // touching intervals — adjacent windows and the overnight pair have
        // already become the single continuous spans they describe. That is
        // what makes the containment test below correct for a booking that runs
        // across 12:00 on a resource with 09:00-12:00 and 12:00-17:00 windows.
        var open = AvailabilityWindowExpansion.ExpandToUtc(
            resource.AvailabilityWindows, fromLocalDate, toLocalDate, zone);

        if (!Covers(open, requested))
        {
            return BookingEligibilityResult.OutsideAvailability;
        }

        // Covered a moment ago and not covered now means a blackout is the
        // difference — which is how this distinguishes the two reasons without
        // testing the blackouts against the request directly. It also gets the
        // partial case right for free: a blackout clipping one minute off the
        // end leaves the request uncovered, and a booking that is 99% legal is
        // still refused (FR-3.4, decision 0001).
        var afterBlackouts = IntervalAlgebra.Subtract(open, blackouts);

        if (!Covers(afterBlackouts, requested))
        {
            return BookingEligibilityResult.BlackoutPeriod;
        }

        // Bookings consume units, not time (decision 0005), so what comes back
        // is the runs where at least `quantity` units are free throughout. If
        // one of them covers the request, every instant of it has room.
        var withRoomForThisBooking = CapacitySweep.Subtract(
            afterBlackouts, bookings, resource.Capacity, quantity);

        if (Covers(withRoomForThisBooking, requested))
        {
            return BookingEligibilityResult.Eligible;
        }

        return NothingFreeAtAll(afterBlackouts, bookings, resource.Capacity, quantity, requested)
            ? BookingEligibilityResult.SlotUnavailable
            : BookingEligibilityResult.CapacityExceeded;
    }

    // Which of the two capacity refusals applies (owner's call, 2026-09-07):
    // nothing free at any instant inside the request → SlotUnavailable;
    // something free throughout but less than asked → CapacityExceeded.
    //
    // A second sweep, at a required quantity of one, rather than reading a
    // number off the first one. That is not laziness — the runs the first sweep
    // returns can be *wider* than the request, and each carries the floor across
    // its whole span, so a run reaching back over a busy morning would report a
    // figure that has nothing to do with the interval actually asked for. Asking
    // the containment question twice is the only reading that stays true.
    //
    // Skipped entirely when the request is for one unit, because the two sweeps
    // would be the same sweep. That is also why an exclusive resource can only
    // ever produce SlotUnavailable.
    private static bool NothingFreeAtAll(
        IReadOnlyList<UtcInterval> open,
        IEnumerable<BookedQuantity> bookings,
        int capacity,
        int quantity,
        UtcInterval requested)
    {
        if (quantity == 1)
        {
            return true;
        }

        var withAnythingFree = CapacitySweep.Subtract(open, bookings, capacity, requiredQuantity: 1);

        return !Covers(withAnythingFree, requested);
    }

    // Wholly inside one of them, not merely overlapping any. The intervals are
    // already merged and non-overlapping at every call site here, so a request
    // that no single one contains is genuinely not contained — it cannot be
    // straddling two spans that are really one.
    private static bool Covers(IEnumerable<UtcInterval> intervals, UtcInterval requested) =>
        intervals.Any(i => i.StartUtc <= requested.StartUtc && i.EndUtc >= requested.EndUtc);

    private static bool Covers(IEnumerable<BookableInterval> intervals, UtcInterval requested) =>
        Covers(intervals.Select(i => i.Interval), requested);
}
