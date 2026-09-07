using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Time;

namespace BookSpace.UnitTests.Availability;

// WP-3 Phase 5 step 1: a weekly schedule in resource-local wall-clock time
// becomes the instants it actually opens for.
//
// Most of these run against a fixed-offset IResourceTimeZone rather than a real
// zone, on purpose — they are about the expansion loop, the merge and the
// midnight convention, and none of that should be able to fail because of
// tzdata. The DST section at the bottom uses the real America/New_York, because
// decision D3's 23- and 25-hour days are the one claim a fake cannot make.
public class AvailabilityWindowExpansionTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

    // 2026-09-07 is a Monday.
    private static readonly DateOnly Monday = new(2026, 9, 7);

    // AvailabilityWindow's constructor is internal to the Domain assembly (WP-3
    // decision D1) — Resource is its only creator — so the schedule is built
    // through the aggregate, which is the path production code takes.
    private static IReadOnlyCollection<AvailabilityWindow> Schedule(
        params (DayOfWeek Weekday, TimeOnly OpensAt, TimeOnly ClosesAt)[] windows)
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, capacity: 4,
            timeZoneId: "America/New_York", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

        return resource.ReplaceAvailabilityWindows(
            windows.Select(w => new AvailabilityWindowDefinition(
                Guid.NewGuid(), w.Weekday, w.OpensAt, w.ClosesAt)),
            ActorId,
            NowUtc);
    }

    private static TimeOnly At(int hour, int minute = 0) => new(hour, minute);

    private static UtcInterval Interval(
        DateOnly startDate, TimeOnly startTime, DateOnly endDate, TimeOnly endTime) =>
        new(
            DateTime.SpecifyKind(startDate.ToDateTime(startTime), DateTimeKind.Utc),
            DateTime.SpecifyKind(endDate.ToDateTime(endTime), DateTimeKind.Utc));

    // ---- The expansion loop -------------------------------------------------

    [Fact]
    public void AResourceWithNoWindowsOpensAtNoTime()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(), Monday, Monday.AddDays(6), Utc);

        Assert.Empty(intervals);
    }

    [Fact]
    public void AWindowProducesOneIntervalOnEveryMatchingDateInRange()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((DayOfWeek.Monday, At(9), At(17))), Monday, Monday.AddDays(14), Utc);

        Assert.Equal(
            new[]
            {
                Interval(Monday, At(9), Monday, At(17)),
                Interval(Monday.AddDays(7), At(9), Monday.AddDays(7), At(17)),
                Interval(Monday.AddDays(14), At(9), Monday.AddDays(14), At(17)),
            },
            intervals);
    }

    [Fact]
    public void ADateWithNoMatchingWeekdayProducesNothing()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((DayOfWeek.Saturday, At(9), At(17))), Monday, Monday.AddDays(4), Utc);

        Assert.Empty(intervals);
    }

    [Fact]
    public void TheRangeIsInclusiveAtBothEnds()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((DayOfWeek.Monday, At(9), At(17))), Monday, Monday, Utc);

        Assert.Equal(Interval(Monday, At(9), Monday, At(17)), Assert.Single(intervals));
    }

    [Fact]
    public void TheLocalWallClockIsConvertedThroughTheResourcesZone()
    {
        // Fixed +02:00, so 09:00 local is 07:00Z.
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((DayOfWeek.Monday, At(9), At(17))),
            Monday,
            Monday,
            new FixedOffsetZone(TimeSpan.FromHours(2)));

        Assert.Equal(Interval(Monday, At(7), Monday, At(15)), Assert.Single(intervals));
    }

    [Fact]
    public void AnInvertedRangeIsRefused()
    {
        Assert.Throws<ArgumentException>(() => AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((DayOfWeek.Monday, At(9), At(17))), Monday.AddDays(1), Monday, Utc));
    }

    // ---- Merging ------------------------------------------------------------

    // Phase 3 allows adjacent windows on one weekday because ClosesAt is
    // exclusive. A member asking what is bookable should see one span.
    [Fact]
    public void AdjacentWindowsOnOneWeekdayBecomeOneInterval()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(
                (DayOfWeek.Monday, At(9), At(12)),
                (DayOfWeek.Monday, At(12), At(17))),
            Monday,
            Monday,
            Utc);

        Assert.Equal(Interval(Monday, At(9), Monday, At(17)), Assert.Single(intervals));
    }

    [Fact]
    public void WindowsWithARealGapBetweenThemStaySeparateAndOrdered()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(
                (DayOfWeek.Monday, At(13), At(17)),
                (DayOfWeek.Monday, At(9), At(12))),
            Monday,
            Monday,
            Utc);

        Assert.Equal(
            new[]
            {
                Interval(Monday, At(9), Monday, At(12)),
                Interval(Monday, At(13), Monday, At(17)),
            },
            intervals);
    }

    // ---- The midnight convention -------------------------------------------

    // CK_AvailabilityWindows_Window requires ClosesAt > OpensAt, so a resource
    // open from 22:00 to 02:00 is stored as two rows on consecutive weekdays.
    // Rejoining them is what makes the overnight span visible as one interval,
    // and it only works because 23:59:59 is read as midnight rather than
    // literally — otherwise there is a one-second hole at every boundary.
    [Fact]
    public void AnOvernightScheduleStoredAsTwoRowsBecomesOneInterval()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(
                (DayOfWeek.Monday, At(22), AvailabilityWindowExpansion.ClosesAtEndOfDay),
                (DayOfWeek.Tuesday, TimeOnly.MinValue, At(2))),
            Monday,
            Monday.AddDays(1),
            Utc);

        Assert.Equal(
            Interval(Monday, At(22), Monday.AddDays(1), At(2)),
            Assert.Single(intervals));
    }

    // A resource that never closes: seven end-of-day windows collapse to one
    // continuous week.
    [Fact]
    public void AScheduleCoveringEveryDayEndToEndBecomesOneInterval()
    {
        var everyDay = Enum.GetValues<DayOfWeek>()
            .Select(d => (d, TimeOnly.MinValue, AvailabilityWindowExpansion.ClosesAtEndOfDay))
            .ToArray();

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(everyDay), Monday, Monday.AddDays(6), Utc);

        Assert.Equal(
            Interval(Monday, TimeOnly.MinValue, Monday.AddDays(7), TimeOnly.MinValue),
            Assert.Single(intervals));
    }

    // The convention applies on its own, not only when there is a next-day
    // window to join: an admin who writes 23:59:59 means midnight, because
    // time(0) gives them no other way to say it.
    [Fact]
    public void AnEndOfDayWindowOnTheLastDateInRangeStillRunsToMidnight()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((DayOfWeek.Monday, At(22), AvailabilityWindowExpansion.ClosesAtEndOfDay)),
            Monday,
            Monday,
            Utc);

        Assert.Equal(
            Interval(Monday, At(22), Monday.AddDays(1), TimeOnly.MinValue),
            Assert.Single(intervals));
    }

    // Only the maximum time(0) value is read as midnight. 23:59:00 is a minute
    // short and means what it says.
    [Fact]
    public void AWindowClosingJustShortOfEndOfDayIsTakenLiterally()
    {
        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(
                (DayOfWeek.Monday, At(22), At(23, 59)),
                (DayOfWeek.Tuesday, TimeOnly.MinValue, At(2))),
            Monday,
            Monday.AddDays(1),
            Utc);

        Assert.Equal(
            new[]
            {
                Interval(Monday, At(22), Monday, At(23, 59)),
                Interval(Monday.AddDays(1), TimeOnly.MinValue, Monday.AddDays(1), At(2)),
            },
            intervals);
    }

    // The range is [fromLocalDate 00:00, toLocalDate+1 00:00) in local time, so
    // the tail of an overnight span that began the day before the range is
    // outside it — and nothing bookable is lost, because that tail ends exactly
    // where the range starts.
    [Fact]
    public void AnOvernightSpanStartingBeforeTheRangeContributesNothingInsideIt()
    {
        var tuesday = Monday.AddDays(1);

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule(
                (DayOfWeek.Monday, At(22), AvailabilityWindowExpansion.ClosesAtEndOfDay),
                (DayOfWeek.Tuesday, TimeOnly.MinValue, At(2))),
            tuesday,
            tuesday,
            Utc);

        Assert.Equal(
            Interval(tuesday, TimeOnly.MinValue, tuesday, At(2)),
            Assert.Single(intervals));
    }

    // ---- Daylight saving (WP-3 decision D3) --------------------------------

    // The whole of D3 in one assertion: the window says "all day" both times and
    // the resource is open for the hours that actually elapsed — 23 on the day
    // the clocks go forward, 25 on the day they go back. No policy, no skipped
    // occurrence, no shifted window.
    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void ADaylightSavingTransitionMakesTheLocalDayShorterOrLonger(
        int year, int month, int day, int expectedHours)
    {
        var date = new DateOnly(year, month, day);

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((date.DayOfWeek, TimeOnly.MinValue, AvailabilityWindowExpansion.ClosesAtEndOfDay)),
            date,
            date,
            NewYork);

        Assert.Equal(TimeSpan.FromHours(expectedHours), Assert.Single(intervals).Duration);
    }

    // The clocks-back hour is the half TimeZoneInfo's defaults would lose: a
    // 01:00-02:00 window on 2026-11-01 lasts two real hours, because 01:00-02:00
    // local happens twice.
    [Fact]
    public void AWindowOverTheRepeatedHourLastsTwiceAsLong()
    {
        var fallBack = new DateOnly(2026, 11, 1);

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((fallBack.DayOfWeek, At(1), At(2))), fallBack, fallBack, NewYork);

        var interval = Assert.Single(intervals);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), interval.StartUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc), interval.EndUtc);
    }

    // A window lying entirely inside the clocks-forward gap describes local
    // times that did not happen that day, so it opens and closes at the same
    // instant and is dropped rather than reported as a zero-length slot.
    [Fact]
    public void AWindowEntirelyInsideTheClocksForwardGapDisappearsForThatDate()
    {
        var springForward = new DateOnly(2026, 3, 8);

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((springForward.DayOfWeek, At(2), At(2, 59))),
            springForward,
            springForward,
            NewYork);

        Assert.Empty(intervals);
    }

    // ...and only for that date. The same window a week later is ordinary.
    [Fact]
    public void TheSameWindowIsUnaffectedOnEveryOtherDate()
    {
        var springForward = new DateOnly(2026, 3, 8);

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((springForward.DayOfWeek, At(2), At(2, 59))),
            springForward,
            springForward.AddDays(7),
            NewYork);

        var interval = Assert.Single(intervals);
        Assert.Equal(new DateTime(2026, 3, 15, 6, 0, 0, DateTimeKind.Utc), interval.StartUtc);
        Assert.Equal(new DateTime(2026, 3, 15, 6, 59, 0, DateTimeKind.Utc), interval.EndUtc);
    }

    // A window straddling the clocks-forward gap keeps its real length:
    // 01:00-04:00 local on 2026-03-08 is two hours, not three.
    [Fact]
    public void AWindowStraddlingTheClocksForwardGapLosesThatHour()
    {
        var springForward = new DateOnly(2026, 3, 8);

        var intervals = AvailabilityWindowExpansion.ExpandToUtc(
            Schedule((springForward.DayOfWeek, At(1), At(4))),
            springForward,
            springForward,
            NewYork);

        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(intervals).Duration);
    }

    private static readonly IResourceTimeZone NewYork =
        new SystemResourceTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static readonly IResourceTimeZone Utc = new FixedOffsetZone(TimeSpan.Zero);

    // A zone with no transitions, so these tests assert the expansion rather
    // than the host's tzdata. The real conversion rules have their own tests in
    // SystemResourceTimeZoneTests.
    private sealed class FixedOffsetZone : IResourceTimeZone
    {
        private readonly TimeSpan _offset;

        public FixedOffsetZone(TimeSpan offset) => _offset = offset;

        public DateTime ToUtcEarliest(DateTime resourceLocal) => ToUtc(resourceLocal);

        public DateTime ToUtcLatest(DateTime resourceLocal) => ToUtc(resourceLocal);

        public DateTime ToLocal(DateTime utc) =>
            DateTime.SpecifyKind(utc + _offset, DateTimeKind.Unspecified);

        private DateTime ToUtc(DateTime resourceLocal) =>
            DateTime.SpecifyKind(resourceLocal - _offset, DateTimeKind.Utc);
    }
}
