using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Time;

namespace BookSpace.UnitTests.Availability;

// The four steps composed (WP-3 Phase 5 step 2): schedule expanded, blackouts
// removed, bookings subtracted in units, too-short spans dropped.
//
// The pieces have their own tests. These are about the composition — that the
// steps run in an order where each one's output is what the next expects, and
// that the whole thing answers the question the endpoint will ask. It is also
// the shape WP-4 will call, so a booking rejection and this response cannot
// disagree.
public class AvailabilityCalculatorTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

    // 2026-09-07 is a Monday. New York is UTC-4 in September, so a 09:00-17:00
    // local window is 13:00Z-21:00Z.
    private static readonly DateOnly Monday = new(2026, 9, 7);

    private static readonly IResourceTimeZone NewYork =
        new SystemResourceTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static Resource Room(
        int capacity = 4,
        int? minDurationMinutes = null,
        params (DayOfWeek Weekday, TimeOnly OpensAt, TimeOnly ClosesAt)[] windows)
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", "Room", capacity,
            timeZoneId: "America/New_York", requiresApproval: false,
            minDurationMinutes: minDurationMinutes, maxDurationMinutes: null,
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

    // Instants on the Monday, for stating expectations. Monday's local
    // 09:00-17:00 is 13:00Z-21:00Z.
    private static DateTime Instant(int hour, int minute = 0, int dayOffset = 0) =>
        new DateTime(2026, 9, 7, hour, minute, 0, DateTimeKind.Utc).AddDays(dayOffset);

    private static UtcInterval Utc(int startHour, int endHour) =>
        new(Instant(startHour), Instant(endHour));

    private static IReadOnlyList<BookableInterval> Calculate(
        Resource resource,
        IEnumerable<UtcInterval>? blackouts = null,
        IEnumerable<BookedQuantity>? bookings = null,
        DateOnly? from = null,
        DateOnly? to = null) =>
        AvailabilityCalculator.BookableIntervals(
            resource,
            from ?? Monday,
            to ?? Monday,
            NewYork,
            blackouts ?? Array.Empty<UtcInterval>(),
            bookings ?? Array.Empty<BookedQuantity>());

    // ---- The plain case ----------------------------------------------------

    [Fact]
    public void AnOpenDayWithNothingAgainstItIsBookableInFull()
    {
        var bookable = Calculate(Room(windows: NineToFive(DayOfWeek.Monday)));

        var interval = Assert.Single(bookable);
        Assert.Equal(Utc(13, 21), interval.Interval);
        Assert.Equal(4, interval.RemainingCapacity);
    }

    [Fact]
    public void AResourceWithNoScheduleIsNeverBookable()
    {
        Assert.Empty(Calculate(Room()));
    }

    // ---- Blackouts (FR-3.4, the AC's "excludes blackout periods") -----------

    [Fact]
    public void ABlackoutRemovesTheTimeItCovers()
    {
        var bookable = Calculate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            blackouts: new[] { Utc(15, 16) });

        Assert.Equal(
            new[]
            {
                new BookableInterval(Utc(13, 15), 4),
                new BookableInterval(Utc(16, 21), 4),
            },
            bookable);
    }

    [Fact]
    public void ABlackoutCoveringTheWholeDayLeavesNothing()
    {
        var bookable = Calculate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            blackouts: new[] { Utc(10, 23) });

        Assert.Empty(bookable);
    }

    // ---- Bookings (the AC's "excludes existing bookings") ------------------

    [Fact]
    public void ABookingReducesTheCapacityLeftForItsSpan()
    {
        var bookable = Calculate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            bookings: new[] { new BookedQuantity(Utc(15, 16), 1) });

        Assert.Equal(
            new[]
            {
                new BookableInterval(Utc(13, 15), 4),
                new BookableInterval(Utc(15, 16), 3),
                new BookableInterval(Utc(16, 21), 4),
            },
            bookable);
    }

    [Fact]
    public void ABookingTakingTheLastUnitRemovesItsSpanEntirely()
    {
        var bookable = Calculate(
            Room(capacity: 1, windows: NineToFive(DayOfWeek.Monday)),
            bookings: new[] { new BookedQuantity(Utc(15, 16), 1) });

        Assert.Equal(
            new[]
            {
                new BookableInterval(Utc(13, 15), 1),
                new BookableInterval(Utc(16, 21), 1),
            },
            bookable);
    }

    // Blackouts before bookings, and it has to be that way round: a booking
    // inside blacked-out time has already been cancelled by decision 0001's
    // cascade, and counting its units against a span that no longer exists
    // would be arithmetic on a row the blackout deleted the meaning of.
    [Fact]
    public void ABlackoutAndABookingOverTheSameSpanLeaveTheSpanGoneOnce()
    {
        var bookable = Calculate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            blackouts: new[] { Utc(15, 16) },
            bookings: new[] { new BookedQuantity(Utc(15, 16), 2) });

        Assert.Equal(
            new[]
            {
                new BookableInterval(Utc(13, 15), 4),
                new BookableInterval(Utc(16, 21), 4),
            },
            bookable);
    }

    // ---- The minimum-duration floor (owner's call, 2026-09-03) -------------

    [Fact]
    public void ASpanShorterThanTheResourcesMinimumDurationIsNotOffered()
    {
        // A blackout from 13:30Z to closing leaves half an hour, against a
        // 60-minute floor.
        var bookable = Calculate(
            Room(minDurationMinutes: 60, windows: NineToFive(DayOfWeek.Monday)),
            blackouts: new[] { new UtcInterval(Instant(13, 30), Instant(21)) });

        Assert.Empty(bookable);
    }

    [Fact]
    public void ASpanExactlyTheMinimumDurationIsOffered()
    {
        var bookable = Calculate(
            Room(minDurationMinutes: 60, windows: NineToFive(DayOfWeek.Monday)),
            blackouts: new[] { Utc(14, 21) });

        Assert.Equal(Utc(13, 14), Assert.Single(bookable).Interval);
    }

    [Fact]
    public void WithNoMinimumDurationEvenAShortSpanIsOffered()
    {
        var bookable = Calculate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            blackouts: new[] { new UtcInterval(Instant(13, 15), Instant(21)) });

        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(bookable).Interval.Duration);
    }

    // The consequence of applying the floor per interval, recorded because it
    // reads as a bug and is the documented behaviour: a one-unit booking in the
    // middle of an open day splits it into three, and the flanking spans can
    // fall under the floor and vanish — even though a one-unit booking across
    // the whole day would be accepted. Inherent in decision D2's response
    // shape, which carries one capacity figure per interval.
    [Fact]
    public void AShortBookingCanHideTimeThatIsStillBookableAtALowerQuantity()
    {
        var bookable = Calculate(
            Room(minDurationMinutes: 240, windows: NineToFive(DayOfWeek.Monday)),
            bookings: new[] { new BookedQuantity(Utc(16, 17), 1) });

        // 13:00-16:00 (3h) and 17:00-21:00 (4h) flank a 16:00-17:00 span with 3
        // units left. Only the last clears the four-hour floor.
        var interval = Assert.Single(bookable);
        Assert.Equal(Utc(17, 21), interval.Interval);
        Assert.Equal(4, interval.RemainingCapacity);
    }

    // ---- Composition over a range ------------------------------------------

    [Fact]
    public void EveryMatchingDateInTheRangeIsAnswered()
    {
        var bookable = Calculate(
            Room(windows: NineToFive(DayOfWeek.Monday)),
            from: Monday,
            to: Monday.AddDays(7));

        Assert.Equal(2, bookable.Count);
        Assert.All(bookable, i => Assert.Equal(TimeSpan.FromHours(8), i.Interval.Duration));
    }

    // Adjacent windows, an overnight pair, a blackout and a booking in one pass,
    // to prove the steps hand off in the right shape rather than each working
    // alone.
    [Fact]
    public void TheStepsComposeOverAScheduleThatNeedsAllOfThem()
    {
        var resource = Room(
            capacity: 2,
            windows:
            [
                (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)),
                (DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(17, 0)),
                (DayOfWeek.Monday, new TimeOnly(22, 0), AvailabilityWindowExpansion.ClosesAtEndOfDay),
                (DayOfWeek.Tuesday, TimeOnly.MinValue, new TimeOnly(2, 0)),
            ]);

        var bookable = AvailabilityCalculator.BookableIntervals(
            resource,
            Monday,
            Monday.AddDays(1),
            NewYork,
            blackouts: [Utc(15, 16)],
            bookings: [new BookedQuantity(Utc(13, 14), 1)]);

        // 09:00-17:00 local is one span after the adjacent windows merge; the
        // blackout splits it; the booking takes a unit out of its first hour.
        // The overnight pair rejoins into a single 22:00-02:00 local span,
        // which is 02:00Z-06:00Z the next day.
        Assert.Equal(
            new[]
            {
                new BookableInterval(Utc(13, 14), 1),
                new BookableInterval(Utc(14, 15), 2),
                new BookableInterval(Utc(16, 21), 2),
                new BookableInterval(
                    new UtcInterval(Instant(2, dayOffset: 1), Instant(6, dayOffset: 1)), 2),
            },
            bookable);
    }

    // ---- What this deliberately does not decide ----------------------------

    // An archived resource is the endpoint's answer to give, not the schedule's:
    // FR-3.5 keeps it readable and its windows still say when it used to open,
    // while WP-4 needs a ResourceArchived rejection rather than silence.
    [Fact]
    public void AnArchivedResourcesScheduleIsStillCalculated()
    {
        var resource = Room(windows: NineToFive(DayOfWeek.Monday));
        resource.Archive(ActorId, NowUtc);

        Assert.Single(Calculate(resource));
    }
}
