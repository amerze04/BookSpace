using BookSpace.Domain.Availability;

namespace BookSpace.UnitTests.Availability;

// The capacity sweep: existing bookings consume *units*, not time, so what comes
// back is intervals carrying what is left of Capacity (decision 0020 over
// docs/decisions/0005-capacity-semantics.md).
//
// **Rewritten 2026-09-04**, when the sweep grew a requiredQuantity parameter.
// Before that it returned the finest partition — cut wherever the remaining
// figure changed — and the caller had to join the pieces. It now answers for a
// stated quantity: a segment that cannot hold that many units is a wall, and
// everything between two walls is one run carrying the floor of what is free
// across it. That change is what made the minimum-duration filter honest; see
// AvailabilityCalculator.
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

    private static IReadOnlyList<BookableInterval> Sweep(
        UtcInterval[] open,
        BookedQuantity[] bookings,
        int capacity = 4,
        int quantity = 1) =>
        CapacitySweep.Subtract(open, bookings, capacity, quantity);

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
        AssertBookable([(9, 17, 4)], Sweep([At(9, 17)], []));
    }

    [Fact]
    public void NoOpenTimeIsNotMadeBookableByHavingCapacity()
    {
        Assert.Empty(Sweep([], []));
    }

    // ---- Partial consumption: the point of the capacity model ---------------

    // A booking of one unit against a capacity of four does not close the slot,
    // and — since this caller wants only one unit — does not even break the span:
    // the whole day is still bookable, with three units free at its tightest
    // point. A boolean free/busy model could express neither half of that.
    [Fact]
    public void ABookingLeavesTheSpanIntactAndLowersTheFloor()
    {
        AssertBookable([(9, 17, 3)], Sweep([At(9, 17)], [Booked(12, 13, 1)]));
    }

    // The same data, asked for four units: now the booked hour *is* a wall,
    // because four units are not free across it. This pair is the whole reason
    // the quantity parameter exists — one list of intervals cannot answer both.
    [Fact]
    public void TheSameBookingIsAWallForACallerWhoNeedsEveryUnit()
    {
        AssertBookable(
            [(9, 12, 4), (13, 17, 4)],
            Sweep([At(9, 17)], [Booked(12, 13, 1)], quantity: 4));
    }

    [Fact]
    public void ConcurrentBookingsSumAgainstCapacity()
    {
        // 12:00-15:00 holds one unit and 13:00-14:00 holds two more, so the
        // tightest hour has one free — and that is the floor for the whole day.
        AssertBookable(
            [(9, 17, 1)],
            Sweep([At(9, 17)], [Booked(12, 15, 1), Booked(13, 14, 2)]));
    }

    // Raising the quantity walls off progressively more of the same day. Read
    // downwards, this is the shape of what the resource can actually take.
    [Theory]
    [InlineData(1, "9-17:1")]
    [InlineData(2, "9-13:3|14-17:3")]
    [InlineData(3, "9-13:3|14-17:3")]
    [InlineData(4, "9-12:4|15-17:4")]
    [InlineData(5, "")]
    public void TheAnswerNarrowsAsTheRequestedQuantityRises(int quantity, string expected)
    {
        var bookable = Sweep(
            [At(9, 17)], [Booked(12, 15, 1), Booked(13, 14, 2)], quantity: quantity);

        Assert.Equal(
            expected,
            string.Join(
                "|",
                bookable.Select(i =>
                    $"{i.Interval.StartUtc.Hour}-{i.Interval.EndUtc.Hour}:{i.RemainingCapacity}")));
    }

    // ---- Full consumption: only now does time disappear --------------------

    [Fact]
    public void TimeDisappearsOnlyOnceTheUnitsRunOut()
    {
        AssertBookable(
            [(9, 12, 4), (13, 17, 4)],
            Sweep([At(9, 17)], [Booked(12, 13, 4)]));
    }

    [Fact]
    public void AFullyBookedDayIsNotBookableAtAll()
    {
        Assert.Empty(Sweep([At(9, 17)], [Booked(9, 17, 4)]));
    }

    // dbo.CreateBooking's range lock is what prevents this (CLAUDE.md §4.1), so
    // it should be unreachable — but this code cannot verify how a row got into
    // the table, and treating a negative remainder as bookable would be worse
    // than treating it as full.
    [Fact]
    public void AnOverbookedSpanIsTreatedAsFullRatherThanTrusted()
    {
        AssertBookable(
            [(9, 12, 4), (13, 17, 4)],
            Sweep([At(9, 17)], [Booked(12, 13, 3), Booked(12, 13, 3)]));
    }

    // ---- Rejoining, and where it stops -------------------------------------

    // Two back-to-back bookings are two boundaries this caller has no reason to
    // see: one unit is free across both, which is all they asked about.
    [Fact]
    public void AdjacentSpansThatCanBothHoldTheBookingAreRejoined()
    {
        AssertBookable(
            [(9, 17, 3)],
            Sweep([At(9, 17)], [Booked(12, 13, 1), Booked(13, 14, 1)]));
    }

    // Never across a wall, which is why segments that cannot hold the booking are
    // dropped *before* the rejoin rather than after — the other order would
    // report one continuous span straight through a fully-booked hour.
    [Fact]
    public void SpansAreNotRejoinedAcrossAFullyBookedGap()
    {
        var bookable = Sweep([At(9, 17)], [Booked(12, 13, 4)]);

        AssertBookable([(9, 12, 4), (13, 17, 4)], bookable);
        Assert.Equal(2, bookable.Count);
    }

    // ...and never across a closing time or a blackout. Two open intervals with
    // identical remaining capacity stay two intervals, because the gap between
    // them is not bookable at any quantity.
    [Fact]
    public void SpansAreNotRejoinedAcrossAGapBetweenOpenIntervals()
    {
        AssertBookable([(9, 12, 4), (13, 17, 4)], Sweep([At(9, 12), At(13, 17)], []));
    }

    // The floor is the *minimum* across a rejoined run — not an average, and not
    // the best part of it. That is what makes the number safe to book against for
    // any sub-span of the interval.
    [Fact]
    public void TheReportedCapacityIsTheFloorAcrossTheWholeRun()
    {
        var interval = Assert.Single(Sweep(
            [At(9, 17)], [Booked(10, 11, 1), Booked(12, 13, 3), Booked(14, 15, 2)]));

        Assert.Equal(At(9, 17), interval.Interval);
        Assert.Equal(1, interval.RemainingCapacity);
    }

    // ---- Bookings outside the open time ------------------------------------

    [Fact]
    public void ABookingOutsideEveryOpenIntervalChangesNothing()
    {
        AssertBookable([(9, 12, 4)], Sweep([At(9, 12)], [Booked(14, 15, 4)]));
    }

    // A booking that began before the range still holds its units inside it —
    // the sweep applies its claim while catching up to the first open interval.
    [Fact]
    public void ABookingStartingBeforeTheOpenTimeStillHoldsItsUnits()
    {
        AssertBookable([(9, 17, 1)], Sweep([At(9, 17)], [Booked(7, 11, 3)]));
    }

    // The same booking, asked for two units: the part it overlaps becomes a wall.
    [Fact]
    public void ABookingStartingBeforeTheOpenTimeCanWallOffItsStart()
    {
        AssertBookable([(11, 17, 4)], Sweep([At(9, 17)], [Booked(7, 11, 3)], quantity: 2));
    }

    [Fact]
    public void ABookingSpanningTheWholeOpenTimeIsHeldThroughout()
    {
        AssertBookable([(9, 17, 3)], Sweep([At(9, 17)], [Booked(7, 19, 1)]));
    }

    // Intervals are half-open, so a booking ending exactly when the resource
    // opens holds nothing inside it, and one starting exactly at closing time
    // likewise.
    [Fact]
    public void ABookingTouchingTheOpenTimeAtEitherEndHoldsNothingInsideIt()
    {
        AssertBookable(
            [(9, 17, 4)],
            Sweep([At(9, 17)], [Booked(7, 9, 4), Booked(17, 19, 4)]));
    }

    // ---- Several open intervals in one pass ---------------------------------

    // The sweep keeps one running total across every open interval, so this is
    // the case that would break if the events and the intervals fell out of step.
    [Fact]
    public void EachOpenIntervalIsMeasuredAgainstTheBookingsThatOverlapIt()
    {
        AssertBookable(
            [(9, 12, 3), (14, 18, 2)],
            Sweep(
                [At(9, 12), At(14, 18)],
                [Booked(10, 11, 1), Booked(15, 20, 2)]));
    }

    // ---- Guards -------------------------------------------------------------

    [Fact]
    public void ACapacityOfZeroOrLessIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sweep([At(9, 17)], [], capacity: 0));
    }

    // CK_Bookings_Quantity: a booking holds at least one unit, so asking what is
    // free for zero of them is meaningless rather than empty.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARequiredQuantityOfZeroOrLessIsRefused(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sweep([At(9, 17)], [], quantity: quantity));
    }

    // Asking for more units than the resource has is not an error — it is simply
    // never satisfiable, which is a true answer rather than a malformed request.
    [Fact]
    public void ARequiredQuantityAboveCapacityYieldsNothing()
    {
        Assert.Empty(Sweep([At(9, 17)], [], capacity: 4, quantity: 5));
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
