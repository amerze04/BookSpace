using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Time;

namespace BookSpace.UnitTests.Availability;

// WP-5, FR-5.1 / FR-6.2: a RecurrenceRule's dates and local times, turned into
// the UTC instants (or skips) each occurrence resolves to.
//
// The loop and the ordinary-instant cases run against a fixed-offset fake, on
// the same reasoning AvailabilityWindowExpansionTests uses: none of that
// should be able to fail because of tzdata. The DST cases use the real
// America/New_York, because decision 0024's "earlier for both ends" claim is
// one a fake with no ambiguous hour cannot make.
public class RecurrenceExpansionTests
{
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IResourceTimeZone Utc = new FixedOffsetZone(TimeSpan.Zero);
    private static readonly IResourceTimeZone NewYork =
        new SystemResourceTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static RecurrenceRule Rule(
        RecurrenceFrequency frequency,
        int intervalValue,
        TimeOnly localStartTime,
        TimeOnly localEndTime,
        DateOnly startDate,
        DateOnly? endDate = null,
        int? occurrenceCount = null) =>
        new(Guid.NewGuid(), ResourceId, UserId, frequency, intervalValue,
            localStartTime, localEndTime, startDate,
            endDate ?? (occurrenceCount is null ? startDate.AddYears(1) : null), occurrenceCount,
            "America/New_York", ActorId, NowUtc);

    private static UtcInterval Interval(DateOnly date, TimeOnly startTime, TimeOnly endTime) =>
        new(
            DateTime.SpecifyKind(date.ToDateTime(startTime), DateTimeKind.Utc),
            DateTime.SpecifyKind(date.ToDateTime(endTime), DateTimeKind.Utc));

    // ---- The loop -------------------------------------------------------------

    [Fact]
    public void Expand_StepsUntilEndDateInclusive()
    {
        var startDate = new DateOnly(2026, 9, 7);
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(9, 0), new TimeOnly(9, 30),
            startDate, endDate: startDate.AddDays(14));

        var occurrences = RecurrenceExpansion.Expand(rule, Utc);

        Assert.Equal(
            new[] { startDate, startDate.AddDays(7), startDate.AddDays(14) },
            occurrences.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public void Expand_ADateOneStepPastEndDateIsNotProduced()
    {
        var startDate = new DateOnly(2026, 9, 7);
        // 13 days: two weekly steps (0, 7) fit, the third (14) does not.
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(9, 0), new TimeOnly(9, 30),
            startDate, endDate: startDate.AddDays(13));

        var occurrences = RecurrenceExpansion.Expand(rule, Utc);

        Assert.Equal(new[] { startDate, startDate.AddDays(7) }, occurrences.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public void Expand_StopsAtOccurrenceCountRatherThanEndDate()
    {
        var startDate = new DateOnly(2026, 9, 7);
        var rule = Rule(
            RecurrenceFrequency.Daily, 2, new TimeOnly(9, 0), new TimeOnly(9, 30),
            startDate, occurrenceCount: 4);

        var occurrences = RecurrenceExpansion.Expand(rule, Utc);

        Assert.Equal(
            new[] { startDate, startDate.AddDays(2), startDate.AddDays(4), startDate.AddDays(6) },
            occurrences.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public void Expand_MonthlyStepsByIntervalValueMonths()
    {
        var startDate = new DateOnly(2026, 1, 31);
        var rule = Rule(
            RecurrenceFrequency.Monthly, 1, new TimeOnly(9, 0), new TimeOnly(9, 30),
            startDate, occurrenceCount: 3);

        var occurrences = RecurrenceExpansion.Expand(rule, Utc);

        // .NET's AddMonths clamps a day that does not exist in the target month
        // (no Feb 31), which is RecurrenceRule's own stepping — this asserts the
        // expansion inherits it rather than reimplementing month arithmetic.
        Assert.Equal(
            new[] { startDate, startDate.AddMonths(1), startDate.AddMonths(2) },
            occurrences.Select(o => o.OccurrenceDate));
    }

    // ---- Ordinary resolution ---------------------------------------------------

    [Fact]
    public void Expand_EveryOccurrenceResolvesToAnInstantWithTheRequestedLocalBounds()
    {
        var startDate = new DateOnly(2026, 9, 7);
        var startTime = new TimeOnly(9, 0);
        var endTime = new TimeOnly(9, 30);
        var rule = Rule(RecurrenceFrequency.Weekly, 1, startTime, endTime, startDate, occurrenceCount: 2);

        var occurrences = RecurrenceExpansion.Expand(rule, Utc);

        Assert.All(occurrences, o => Assert.Equal(RecurrenceOccurrenceOutcome.Instant, o.Outcome));
        Assert.Equal(Interval(startDate, startTime, endTime), occurrences[0].Interval);
        Assert.Equal(Interval(startDate.AddDays(7), startTime, endTime), occurrences[1].Interval);
    }

    [Fact]
    public void Expand_OnARealZoneAnOrdinaryOccurrenceMatchesPlainConversion()
    {
        var startDate = new DateOnly(2026, 9, 7); // no DST transition nearby
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(9, 0), new TimeOnly(9, 30),
            startDate, occurrenceCount: 1);

        var occurrence = Assert.Single(RecurrenceExpansion.Expand(rule, NewYork));

        Assert.Equal(RecurrenceOccurrenceOutcome.Instant, occurrence.Outcome);
        Assert.Equal(
            new UtcInterval(
                new DateTime(2026, 9, 7, 13, 0, 0, DateTimeKind.Utc),   // 09:00 EDT (-04:00)
                new DateTime(2026, 9, 7, 13, 30, 0, DateTimeKind.Utc)),
            occurrence.Interval);
    }

    // ---- Decision 0008: spring-forward gap -------------------------------------

    [Fact]
    public void Expand_SkipsAnOccurrenceWhoseLocalStartFallsInTheSpringForwardGap()
    {
        // 2026-03-08: America/New_York springs forward at 02:00 -> 03:00.
        var startDate = new DateOnly(2026, 3, 8);
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(2, 15), new TimeOnly(2, 45),
            startDate, occurrenceCount: 1);

        var occurrence = Assert.Single(RecurrenceExpansion.Expand(rule, NewYork));

        Assert.Equal(RecurrenceOccurrenceOutcome.SkippedSpringForwardGap, occurrence.Outcome);
        Assert.Null(occurrence.Interval);
        Assert.Equal(startDate, occurrence.OccurrenceDate);
    }

    // The end alone falling in the gap is just as fatal to the occurrence as
    // the start doing so — a booking cannot end at a time that never happened
    // either.
    [Fact]
    public void Expand_SkipsAnOccurrenceWhoseLocalEndFallsInTheGapEvenThoughStartDoesNot()
    {
        var startDate = new DateOnly(2026, 3, 8);
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(1, 30), new TimeOnly(2, 30),
            startDate, occurrenceCount: 1);

        var occurrence = Assert.Single(RecurrenceExpansion.Expand(rule, NewYork));

        Assert.Equal(RecurrenceOccurrenceOutcome.SkippedSpringForwardGap, occurrence.Outcome);
    }

    [Fact]
    public void Expand_OtherOccurrencesInTheSameSeriesAreUnaffectedByOneSkippedDate()
    {
        // Weekly from the Sunday before spring-forward: the second occurrence
        // lands exactly on the gap, the first and third do not.
        var startDate = new DateOnly(2026, 3, 1);
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(2, 15), new TimeOnly(2, 45),
            startDate, occurrenceCount: 3);

        var occurrences = RecurrenceExpansion.Expand(rule, NewYork);

        Assert.Equal(
            new[]
            {
                RecurrenceOccurrenceOutcome.Instant,
                RecurrenceOccurrenceOutcome.SkippedSpringForwardGap,
                RecurrenceOccurrenceOutcome.Instant,
            },
            occurrences.Select(o => o.Outcome));
    }

    // ---- Decision 0024: fall-back ambiguity ------------------------------------

    [Fact]
    public void Expand_ResolvesAnAmbiguousOccurrenceUsingTheEarlierInstantForBothEnds()
    {
        // 2026-11-01: America/New_York falls back at 02:00 -> 01:00, so 01:00-01:59
        // happens twice. Both ends here fall inside the repeated hour.
        var startDate = new DateOnly(2026, 11, 1);
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(1, 15), new TimeOnly(1, 45),
            startDate, occurrenceCount: 1);

        var occurrence = Assert.Single(RecurrenceExpansion.Expand(rule, NewYork));

        Assert.Equal(RecurrenceOccurrenceOutcome.Instant, occurrence.Outcome);
        Assert.Equal(
            new UtcInterval(
                new DateTime(2026, 11, 1, 5, 15, 0, DateTimeKind.Utc),  // 01:15 EDT (-04:00), the earlier instant
                new DateTime(2026, 11, 1, 5, 45, 0, DateTimeKind.Utc)), // 01:45 EDT, likewise earlier
            occurrence.Interval);
        // The nominal 30-minute duration survives because both ends resolved
        // under the same (pre-transition) offset — using ToUtcLatest for the
        // end, 0021's range rule, would have stretched this to 1h30.
        Assert.Equal(TimeSpan.FromMinutes(30), occurrence.Interval!.Value.Duration);
    }

    // The half of the pair 0024's record calls out explicitly: mixing
    // earlier-start with later-end would silently grow the occurrence by an
    // hour on this one date a year. Also proves EndUtc > StartUtc still holds
    // when only the start is ambiguous.
    [Fact]
    public void Expand_AnAmbiguousStartWithAnUnambiguousEndProducesAWiderButValidInterval()
    {
        var startDate = new DateOnly(2026, 11, 1);
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(1, 30), new TimeOnly(2, 15),
            startDate, occurrenceCount: 1);

        var occurrence = Assert.Single(RecurrenceExpansion.Expand(rule, NewYork));

        Assert.Equal(RecurrenceOccurrenceOutcome.Instant, occurrence.Outcome);
        Assert.Equal(
            new UtcInterval(
                new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),  // 01:30 EDT, earlier of the pair
                new DateTime(2026, 11, 1, 7, 15, 0, DateTimeKind.Utc)), // 02:15 EST, unambiguous
            occurrence.Interval);
    }

    // ---- Argument guards --------------------------------------------------------

    [Fact]
    public void Expand_ThrowsOnNullRule()
    {
        Assert.Throws<ArgumentNullException>(() => RecurrenceExpansion.Expand(null!, Utc));
    }

    [Fact]
    public void Expand_ThrowsOnNullZone()
    {
        var rule = Rule(
            RecurrenceFrequency.Weekly, 1, new TimeOnly(9, 0), new TimeOnly(9, 30),
            new DateOnly(2026, 9, 7), occurrenceCount: 1);

        Assert.Throws<ArgumentNullException>(() => RecurrenceExpansion.Expand(rule, null!));
    }

    // A zone with no transitions, so the loop/resolution tests above assert the
    // expansion rather than the host's tzdata. The real DST rules have their
    // own tests in SystemResourceTimeZoneTests, reused here for the gap and
    // ambiguity cases.
    private sealed class FixedOffsetZone : IResourceTimeZone
    {
        private readonly TimeSpan _offset;

        public FixedOffsetZone(TimeSpan offset) => _offset = offset;

        public DateTime ToUtcEarliest(DateTime resourceLocal) => ToUtc(resourceLocal);

        public DateTime ToUtcLatest(DateTime resourceLocal) => ToUtc(resourceLocal);

        public DateTime ToLocal(DateTime utc) =>
            DateTime.SpecifyKind(utc + _offset, DateTimeKind.Unspecified);

        public bool IsInvalidLocalTime(DateTime resourceLocal) => false;

        private DateTime ToUtc(DateTime resourceLocal) =>
            DateTime.SpecifyKind(resourceLocal - _offset, DateTimeKind.Utc);
    }
}
