using BookSpace.Domain.Availability;

namespace BookSpace.UnitTests.Availability;

// The capacity sweep (WP-3 Phase 5 step 2): existing bookings consume *units*,
// not time, so what comes back is intervals carrying what is left of Capacity
// (decision D2 over docs/decisions/0005-capacity-semantics.md).
//
// All in plain UTC with no timezone anywhere — the sweep runs after the
// conversion, and mixing the two in one test would hide which half failed.
public class CapacitySweepTests
{
    private static UtcInterval At(int startHour, int endHour) =>
        new(
            new DateTime(2026, 9, 7, startHour, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 7, endHour, 0, 0, DateTimeKind.Utc));

    private static BookedQuantity Booked(int startHour, int endHour, int quantity) =>
        new(At(startHour, endHour), quantity);

    private static void AssertBookable(
        (int StartHour, int EndHour, int RemainingCapacity)[] expected,
        IReadOnlyList<BookableInterval> actual)
    {
        Assert.Equal(
            expected.Select(e => new BookableInterval(At(e.StartHour, e.EndHour), e.RemainingCapacity)),
            actual);
    }

    // ---- No bookings --------------------------------------------------------

    [Fact]
    public void OpenTimeWithNoBookingsKeepsTheWholeCapacity()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, Array.Empty<BookedQuantity>(), capacity: 4);

        AssertBookable([(9, 17, 4)], bookable);
    }

    [Fact]
    public void NoOpenTimeIsNotMadeBookableByHavingCapacity()
    {
        var bookable = CapacitySweep.Subtract(
            Array.Empty<UtcInterval>(), Array.Empty<BookedQuantity>(), capacity: 4);

        Assert.Empty(bookable);
    }

    // ---- Partial consumption: the point of the capacity model ---------------

    // A booking of one unit against a capacity of four does not close the slot.
    // The time stays open with three units left, which a boolean free/busy model
    // could not express.
    [Fact]
    public void ABookingConsumesUnitsRatherThanRemovingTime()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, new[] { Booked(12, 13, 1) }, capacity: 4);

        AssertBookable([(9, 12, 4), (12, 13, 3), (13, 17, 4)], bookable);
    }

    [Fact]
    public void ConcurrentBookingsSumAgainstCapacity()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) },
            new[] { Booked(12, 15, 1), Booked(13, 14, 2) },
            capacity: 4);

        AssertBookable([(9, 12, 4), (12, 13, 3), (13, 14, 1), (14, 15, 3), (15, 17, 4)], bookable);
    }

    // ---- Full consumption: only now does time disappear --------------------

    [Fact]
    public void TimeDisappearsOnlyOnceTheUnitsRunOut()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, new[] { Booked(12, 13, 4) }, capacity: 4);

        AssertBookable([(9, 12, 4), (13, 17, 4)], bookable);
    }

    [Fact]
    public void AFullyBookedDayIsNotBookableAtAll()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, new[] { Booked(9, 17, 4) }, capacity: 4);

        Assert.Empty(bookable);
    }

    // dbo.CreateBooking's range lock is what prevents this (CLAUDE.md §4.1), so
    // it should be unreachable — but this code cannot verify how a row got into
    // the table, and treating a negative remainder as bookable would be worse
    // than treating it as full.
    [Fact]
    public void AnOverbookedSpanIsTreatedAsFullRatherThanTrusted()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) },
            new[] { Booked(12, 13, 3), Booked(12, 13, 3) },
            capacity: 4);

        AssertBookable([(9, 12, 4), (13, 17, 4)], bookable);
    }

    // ---- Rejoining, and where it stops -------------------------------------

    // Two back-to-back bookings of the same size are one boundary the client has
    // no reason to see: the figure never changes across it.
    [Fact]
    public void AdjacentSpansWithTheSameRemainingCapacityAreRejoined()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) },
            new[] { Booked(12, 13, 1), Booked(13, 14, 1) },
            capacity: 4);

        AssertBookable([(9, 12, 4), (12, 14, 3), (14, 17, 4)], bookable);
    }

    // ...but never across a fully-booked gap, which is why the zero-capacity
    // spans are dropped after rejoining rather than before.
    [Fact]
    public void SpansAreNotRejoinedAcrossAFullyBookedGap()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, new[] { Booked(12, 13, 4) }, capacity: 4);

        AssertBookable([(9, 12, 4), (13, 17, 4)], bookable);
        Assert.Equal(2, bookable.Count);
    }

    // ...and never across a closing time or a blackout. Two open intervals with
    // identical remaining capacity stay two intervals, because the gap between
    // them is not bookable.
    [Fact]
    public void SpansAreNotRejoinedAcrossAGapBetweenOpenIntervals()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 12), At(13, 17) }, Array.Empty<BookedQuantity>(), capacity: 4);

        AssertBookable([(9, 12, 4), (13, 17, 4)], bookable);
    }

    // ---- Bookings outside the open time ------------------------------------

    [Fact]
    public void ABookingOutsideEveryOpenIntervalChangesNothing()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 12) }, new[] { Booked(14, 15, 4) }, capacity: 4);

        AssertBookable([(9, 12, 4)], bookable);
    }

    // A booking that began before the range still holds its units inside it —
    // the sweep applies its claim while catching up to the first open interval.
    [Fact]
    public void ABookingStartingBeforeTheOpenTimeStillHoldsItsUnits()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, new[] { Booked(7, 11, 3) }, capacity: 4);

        AssertBookable([(9, 11, 1), (11, 17, 4)], bookable);
    }

    [Fact]
    public void ABookingSpanningTheWholeOpenTimeIsHeldThroughout()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) }, new[] { Booked(7, 19, 1) }, capacity: 4);

        AssertBookable([(9, 17, 3)], bookable);
    }

    // Intervals are half-open, so a booking ending exactly when the resource
    // opens holds nothing inside it, and one starting exactly at closing time
    // likewise.
    [Fact]
    public void ABookingTouchingTheOpenTimeAtEitherEndHoldsNothingInsideIt()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 17) },
            new[] { Booked(7, 9, 4), Booked(17, 19, 4) },
            capacity: 4);

        AssertBookable([(9, 17, 4)], bookable);
    }

    // ---- Several open intervals in one pass ---------------------------------

    // The sweep keeps one running total across every open interval, so this is
    // the case that would break if the events and the intervals fell out of step.
    [Fact]
    public void EachOpenIntervalIsMeasuredAgainstTheBookingsThatOverlapIt()
    {
        var bookable = CapacitySweep.Subtract(
            new[] { At(9, 12), At(14, 18) },
            new[] { Booked(10, 11, 1), Booked(15, 20, 2) },
            capacity: 4);

        AssertBookable([(9, 10, 4), (10, 11, 3), (11, 12, 4), (14, 15, 4), (15, 18, 2)], bookable);
    }

    // ---- Guards -------------------------------------------------------------

    [Fact]
    public void ACapacityOfZeroOrLessIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CapacitySweep.Subtract(
            new[] { At(9, 17) }, Array.Empty<BookedQuantity>(), capacity: 0));
    }

    [Fact]
    public void ABookingOfZeroOrFewerUnitsCannotBeStated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BookedQuantity(At(9, 17), quantity: 0));
    }

    [Fact]
    public void ABookableIntervalWithNothingLeftCannotBeStated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BookableInterval(At(9, 17), remainingCapacity: 0));
    }
}
