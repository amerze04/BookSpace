using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Time;

namespace BookSpace.UnitTests.Availability;

// WP-4 Phase 1a: the same four inputs the availability query takes, asked about
// one interval instead of a range, and answering *which* rule refused it.
//
// Two things these tests are really for. The obvious one is that each rejection
// is reported for the right reason and in the right order. The less obvious one
// is that this and AvailabilityCalculator cannot disagree — an interval the
// query offers must be bookable, and an interval it withholds must not be — so
// several tests below assert both sides of that against the same arrangement.
public class BookingEligibilityTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

    // 2026-09-07 is a Monday. New York is UTC-4 in September, so a local
    // 09:00-17:00 window is 13:00Z-21:00Z — the same arrangement
    // AvailabilityCalculatorTests uses, on purpose.
    private static readonly IResourceTimeZone NewYork =
        new SystemResourceTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static Resource Room(
        int capacity = 4,
        params (DayOfWeek Weekday, TimeOnly OpensAt, TimeOnly ClosesAt)[] windows)
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, capacity,
            timeZoneId: "America/New_York", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

        resource.ReplaceAvailabilityWindows(
            windows.Select(w => new AvailabilityWindowDefinition(
                Guid.NewGuid(), w.Weekday, w.OpensAt, w.ClosesAt)),
            ActorId,
            NowUtc);

        return resource;
    }

    private static (DayOfWeek, TimeOnly, TimeOnly) NineToFive(DayOfWeek weekday) =>
        (weekday, new TimeOnly(9, 0), new TimeOnly(17, 0));

    private static DateTime Instant(int hour, int minute = 0, int dayOffset = 0) =>
        new DateTime(2026, 9, 7, hour, minute, 0, DateTimeKind.Utc).AddDays(dayOffset);

    private static UtcInterval Utc(int startHour, int endHour) =>
        new(Instant(startHour), Instant(endHour));

    private static BookedQuantity Booked(int startHour, int endHour, int quantity) =>
        new(Utc(startHour, endHour), quantity);

    private static BookingEligibilityResult Evaluate(
        Resource resource,
        UtcInterval requested,
        int quantity = 1,
        IEnumerable<UtcInterval>? blackouts = null,
        IEnumerable<BookedQuantity>? bookings = null) =>
        BookingEligibility.Evaluate(
            resource,
            requested,
            quantity,
            NewYork,
            blackouts ?? Array.Empty<UtcInterval>(),
            bookings ?? Array.Empty<BookedQuantity>());

    // ---- The plain case ----------------------------------------------------

    [Fact]
    public void AnIntervalInsideAnOpenDayWithNothingAgainstItIsEligible()
    {
        var result = Evaluate(Room(windows: NineToFive(DayOfWeek.Monday)), Utc(14, 15));

        Assert.Equal(BookingEligibilityResult.Eligible, result);
    }

    [Fact]
    public void TheWholeOpenSpanIsEligible()
    {
        var result = Evaluate(Room(windows: NineToFive(DayOfWeek.Monday)), Utc(13, 21));

        Assert.Equal(BookingEligibilityResult.Eligible, result);
    }

    // ---- Outside availability ---------------------------------------------

    [Fact]
    public void AResourceWithNoScheduleAcceptsNothing()
    {
        var result = Evaluate(Room(), Utc(14, 15));

        Assert.Equal(BookingEligibilityResult.OutsideAvailability, result);
    }

    [Fact]
    public void AnIntervalOnAClosedDayIsOutsideAvailability()
    {
        // Tuesday, on a resource that only opens on Monday.
        var result = Evaluate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            new UtcInterval(Instant(14, dayOffset: 1), Instant(15, dayOffset: 1)));

        Assert.Equal(BookingEligibilityResult.OutsideAvailability, result);
    }

    // Partly open is not open. This is the case a naive "does it overlap the
    // schedule" test gets wrong, and it is the whole reason Covers asks for
    // containment rather than intersection.
    [Theory]
    [InlineData(12, 14)] // starts an hour before opening
    [InlineData(20, 22)] // runs an hour past closing
    [InlineData(12, 22)] // swallows the open span entirely
    public void AnIntervalOnlyPartlyInsideTheScheduleIsOutsideAvailability(int startHour, int endHour)
    {
        var result = Evaluate(Room(windows: NineToFive(DayOfWeek.Monday)), Utc(startHour, endHour));

        Assert.Equal(BookingEligibilityResult.OutsideAvailability, result);
    }

    // Adjacent windows are one continuous span (Phase 3 allows 09:00-12:00 and
    // 12:00-17:00 on one weekday, because ClosesAt is exclusive). A booking
    // running across the join must not be refused for a wall that is not there —
    // this works only because ExpandToUtc merges touching intervals.
    [Fact]
    public void ABookingAcrossTwoAdjacentWindowsIsEligible()
    {
        var resource = Room(
            windows:
            [
                (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)),
                (DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(17, 0)),
            ]);

        // 15:30Z-16:30Z is local 11:30-12:30, straddling the join.
        var result = Evaluate(resource, new UtcInterval(Instant(15, 30), Instant(16, 30)));

        Assert.Equal(BookingEligibilityResult.Eligible, result);
    }

    // A genuine gap in the day is a wall, and the contrast with the test above
    // is the point: touching windows join, separated ones do not.
    [Fact]
    public void ABookingAcrossALunchtimeGapIsOutsideAvailability()
    {
        var resource = Room(
            windows:
            [
                (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)),
                (DayOfWeek.Monday, new TimeOnly(13, 0), new TimeOnly(17, 0)),
            ]);

        var result = Evaluate(resource, new UtcInterval(Instant(15, 30), Instant(17, 30)));

        Assert.Equal(BookingEligibilityResult.OutsideAvailability, result);
    }

    // ---- Blackouts ---------------------------------------------------------

    [Fact]
    public void AnIntervalInsideABlackoutIsRefusedForTheBlackout()
    {
        var result = Evaluate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            blackouts: [Utc(14, 17)]);

        Assert.Equal(BookingEligibilityResult.BlackoutPeriod, result);
    }

    // One minute of overlap is enough. A booking that is 99% legal is still
    // refused — decision 0001 gives a blackout absolute priority, and there is
    // no truncation anywhere in this system.
    [Theory]
    [InlineData(14, 16)] // clipped at the start
    [InlineData(16, 18)] // clipped at the end
    public void AnIntervalPartlyCoveredByABlackoutIsRefusedForTheBlackout(int startHour, int endHour)
    {
        var result = Evaluate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            Utc(startHour, endHour),
            blackouts: [Utc(15, 17)]);

        Assert.Equal(BookingEligibilityResult.BlackoutPeriod, result);
    }

    // Half-open intervals: a blackout ending exactly when the booking starts
    // covers none of it.
    [Fact]
    public void ABlackoutThatMerelyTouchesTheIntervalDoesNotRefuseIt()
    {
        var result = Evaluate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            Utc(16, 17),
            blackouts: [Utc(14, 16)]);

        Assert.Equal(BookingEligibilityResult.Eligible, result);
    }

    // Overlapping blackouts are legal on one resource (decision 0019) and need
    // no special handling — Subtract merges them first.
    [Fact]
    public void OverlappingBlackoutsAreTreatedAsTheirUnion()
    {
        var result = Evaluate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 18),
            blackouts: [Utc(14, 16), Utc(15, 19)]);

        Assert.Equal(BookingEligibilityResult.BlackoutPeriod, result);
    }

    // ---- Capacity ----------------------------------------------------------

    [Fact]
    public void AnIntervalWithOneUnitOfFourTakenIsStillEligible()
    {
        var result = Evaluate(
            Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            bookings: [Booked(15, 16, 1)]);

        Assert.Equal(BookingEligibilityResult.Eligible, result);
    }

    [Fact]
    public void AnExclusiveResourceAlreadyBookedIsSlotUnavailable()
    {
        var result = Evaluate(
            Room(capacity: 1, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            bookings: [Booked(15, 16, 1)]);

        Assert.Equal(BookingEligibilityResult.SlotUnavailable, result);
    }

    // Concurrent bookings sum against Capacity (decision 0005), so four
    // single-unit holds fill a capacity of four just as one four-unit hold would.
    [Fact]
    public void ConcurrentBookingsSumToFillTheCapacity()
    {
        var result = Evaluate(
            Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            bookings: [Booked(15, 16, 1), Booked(15, 16, 1), Booked(15, 16, 2)]);

        Assert.Equal(BookingEligibilityResult.SlotUnavailable, result);
    }

    // A single busy minute in the middle refuses the whole interval: capacity
    // has to hold for every instant, not on average.
    [Fact]
    public void AnIntervalIsRefusedWhenCapacityRunsOutForOnlyPartOfIt()
    {
        var result = Evaluate(
            Room(capacity: 1, windows: NineToFive(DayOfWeek.Monday)),
            Utc(14, 18),
            bookings: [Booked(15, 16, 1)]);

        Assert.Equal(BookingEligibilityResult.SlotUnavailable, result);
    }

    // ---- The two capacity refusals apart -----------------------------------

    // The owner's split of 2026-09-07: something is free throughout, just not
    // enough of it.
    [Fact]
    public void AskingForMoreUnitsThanRemainIsCapacityExceeded()
    {
        var result = Evaluate(
            Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            quantity: 3,
            bookings: [Booked(15, 16, 2)]);

        Assert.Equal(BookingEligibilityResult.CapacityExceeded, result);
    }

    // Nothing free at all, even though several units were asked for — the
    // request's size is not what distinguishes the two codes, the remaining
    // capacity is.
    [Fact]
    public void AskingForSeveralUnitsWhenNoneRemainIsSlotUnavailable()
    {
        var result = Evaluate(
            Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            quantity: 3,
            bookings: [Booked(15, 16, 4)]);

        Assert.Equal(BookingEligibilityResult.SlotUnavailable, result);
    }

    // More than the resource could ever hold, with nothing booked. Not a race
    // and not a Validation failure — the request is well-formed and the answer
    // depends on the resource, so it comes back as the ordinary capacity code.
    [Fact]
    public void AskingForMoreUnitsThanTheResourceHasIsCapacityExceeded()
    {
        var result = Evaluate(
            Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            quantity: 5);

        Assert.Equal(BookingEligibilityResult.CapacityExceeded, result);
    }

    // An exclusive resource can never produce CapacityExceeded, because the only
    // quantity it accepts is 1 and one unit is either there or it is not. This
    // is what the second sweep is skipped for.
    [Fact]
    public void AnExclusiveResourceNeverReportsCapacityExceeded()
    {
        var result = Evaluate(
            Room(capacity: 1, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            bookings: [Booked(15, 16, 1)]);

        Assert.NotEqual(BookingEligibilityResult.CapacityExceeded, result);
    }

    // The floor is measured across the *requested* interval, not across whatever
    // wider run of free time it sits in. A busy morning must not make a quiet
    // afternoon look full — the bug the two-sweep approach exists to avoid.
    [Fact]
    public void ABusyEarlierHourDoesNotLowerTheAnswerForALaterOne()
    {
        var result = Evaluate(
            Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday)),
            Utc(17, 18),
            quantity: 3,
            bookings: [Booked(13, 14, 3)]);

        Assert.Equal(BookingEligibilityResult.Eligible, result);
    }

    // ---- The order of the reasons ------------------------------------------

    // All three rules broken at once. The answer must be the same every time,
    // and it must be the most structural one: "we are never open then" stays
    // true tomorrow, while "someone has it" does not.
    [Fact]
    public void AvailabilityIsReportedAheadOfBlackoutAndCapacity()
    {
        var result = Evaluate(
            Room(capacity: 1, windows: NineToFive(DayOfWeek.Monday)),
            Utc(21, 22), // after closing
            blackouts: [Utc(21, 22)],
            bookings: [Booked(21, 22, 1)]);

        Assert.Equal(BookingEligibilityResult.OutsideAvailability, result);
    }

    [Fact]
    public void BlackoutIsReportedAheadOfCapacity()
    {
        var result = Evaluate(
            Room(capacity: 1, windows: NineToFive(DayOfWeek.Monday)),
            Utc(15, 16),
            blackouts: [Utc(15, 16)],
            bookings: [Booked(15, 16, 1)]);

        Assert.Equal(BookingEligibilityResult.BlackoutPeriod, result);
    }

    // ---- Agreement with the availability query -----------------------------

    // The contract between WP-3's read side and WP-4's write side, asserted
    // directly: every interval the query advertises is one this function
    // accepts. If these two ever drift, the API offers a slot and then refuses
    // to book it — the failure AvailabilityCalculator's header exists to
    // prevent.
    [Fact]
    public void EveryIntervalTheAvailabilityQueryOffersIsEligible()
    {
        var resource = Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday));
        var blackouts = new[] { Utc(15, 16) };
        var bookings = new[] { Booked(17, 19, 3) };
        var monday = new DateOnly(2026, 9, 7);

        var offered = AvailabilityCalculator.BookableIntervals(
            resource, monday, monday, NewYork, blackouts, bookings, requiredQuantity: 1);

        Assert.NotEmpty(offered);
        Assert.All(offered, interval => Assert.Equal(
            BookingEligibilityResult.Eligible,
            Evaluate(resource, interval.Interval, quantity: 1, blackouts: blackouts, bookings: bookings)));
    }

    // And the same at a quantity the query was asked about, which is the case
    // decision 0020's amendment added.
    [Fact]
    public void EveryIntervalOfferedForAQuantityIsEligibleAtThatQuantity()
    {
        var resource = Room(capacity: 4, windows: NineToFive(DayOfWeek.Monday));
        var bookings = new[] { Booked(15, 16, 2) };
        var monday = new DateOnly(2026, 9, 7);

        var offered = AvailabilityCalculator.BookableIntervals(
            resource, monday, monday, NewYork, [], bookings, requiredQuantity: 3);

        Assert.NotEmpty(offered);
        Assert.All(offered, interval => Assert.Equal(
            BookingEligibilityResult.Eligible,
            Evaluate(resource, interval.Interval, quantity: 3, bookings: bookings)));
    }

    // ---- Guards ------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AQuantityOfZeroOrLessIsRejected(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Evaluate(Room(windows: NineToFive(DayOfWeek.Monday)), Utc(14, 15), quantity));
    }

    [Fact]
    public void TheResourceIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => BookingEligibility.Evaluate(
            null!, Utc(14, 15), 1, NewYork, [], []));
    }

    [Fact]
    public void TheZoneIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => BookingEligibility.Evaluate(
            Room(windows: NineToFive(DayOfWeek.Monday)), Utc(14, 15), 1, null!, [], []));
    }
}
