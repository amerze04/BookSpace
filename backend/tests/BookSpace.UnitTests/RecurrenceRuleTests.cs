using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class RecurrenceRuleTests
{
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly StartDate = new(2026, 8, 24);
    private static readonly TimeOnly StartTime = new(9, 0);
    private static readonly TimeOnly EndTime = new(9, 30);

    private static RecurrenceRule CreateValid(DateOnly? endDate = null, int? occurrenceCount = null) =>
        new(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 1, StartTime, EndTime,
            StartDate, endDate ?? StartDate.AddYears(1), endDate is null ? occurrenceCount : null,
            "America/New_York", ActorId, NowUtc);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ThrowsOnNonPositiveIntervalValue(int intervalValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, intervalValue,
                StartTime, EndTime, StartDate, StartDate.AddYears(1), null, "America/New_York", ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankTimeZoneId(string timeZoneId)
    {
        Assert.Throws<ArgumentException>(() =>
            new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 1,
                StartTime, EndTime, StartDate, StartDate.AddYears(1), null, timeZoneId, ActorId, NowUtc));
    }

    [Fact]
    public void Constructor_Throws_WhenBothEndDateAndOccurrenceCountAreSet()
    {
        Assert.Throws<ArgumentException>(() =>
            new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 1,
                StartTime, EndTime, StartDate, StartDate.AddYears(1), 10, "America/New_York", ActorId, NowUtc));
    }

    [Fact]
    public void Constructor_Throws_WhenNeitherEndDateNorOccurrenceCountIsSet()
    {
        Assert.Throws<ArgumentException>(() =>
            new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 1,
                StartTime, EndTime, StartDate, null, null, "America/New_York", ActorId, NowUtc));
    }

    [Theory]
    [InlineData(9, 0, 9, 0)]  // equal
    [InlineData(9, 30, 9, 0)] // end before start
    public void Constructor_Throws_WhenLocalEndTimeIsNotAfterLocalStartTime(
        int startHour, int startMinute, int endHour, int endMinute)
    {
        Assert.Throws<ArgumentException>(() =>
            new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 1,
                new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute),
                StartDate, StartDate.AddYears(1), null, "America/New_York", ActorId, NowUtc));
    }

    [Fact]
    public void Constructor_Succeeds_WhenEndDateIsExactlyTwoYearsAfterStartDate()
    {
        var rule = CreateValid(endDate: StartDate.AddYears(2));

        Assert.Equal(StartDate.AddYears(2), rule.EndDate);
    }

    [Fact]
    public void Constructor_Throws_WhenEndDateIsOneDayPastTwoYearCap()
    {
        Assert.Throws<ArgumentException>(() => CreateValid(endDate: StartDate.AddYears(2).AddDays(1)));
    }

    [Theory]
    [InlineData(RecurrenceFrequency.Daily, 100)]   // ~99 days, well under the cap
    [InlineData(RecurrenceFrequency.Weekly, 50)]   // ~49 weeks, well under the cap
    [InlineData(RecurrenceFrequency.Monthly, 12)]  // 11 months, well under the cap
    public void Constructor_Succeeds_WhenOccurrenceCountImpliesSpanUnderTwoYears(RecurrenceFrequency frequency, int occurrenceCount)
    {
        var rule = new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, frequency, 1,
            StartTime, EndTime, StartDate, null, occurrenceCount, "America/New_York", ActorId, NowUtc);

        Assert.Equal(occurrenceCount, rule.OccurrenceCount);
    }

    [Theory]
    [InlineData(RecurrenceFrequency.Daily, 800)]   // ~799 days, over the cap
    [InlineData(RecurrenceFrequency.Weekly, 200)]  // ~199 weeks, over the cap
    [InlineData(RecurrenceFrequency.Monthly, 30)]  // 29 months, over the cap
    public void Constructor_Throws_WhenOccurrenceCountImpliesSpanOverTwoYears(RecurrenceFrequency frequency, int occurrenceCount)
    {
        Assert.Throws<ArgumentException>(() =>
            new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, frequency, 1,
                StartTime, EndTime, StartDate, null, occurrenceCount, "America/New_York", ActorId, NowUtc));
    }

    [Fact]
    public void Constructor_SetsStatusActive()
    {
        var rule = CreateValid();

        Assert.Equal(RecurrenceStatus.Active, rule.Status);
    }

    // ---- OccurrenceDate(index) -----------------------------------------------
    // The same stepping ComputeImpliedEndDate already uses for the span cap,
    // exposed for RecurrenceExpansion (WP-5) to walk the whole series with.

    [Fact]
    public void OccurrenceDate_StepsDailyByIntervalValue()
    {
        var rule = new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Daily, 3,
            StartTime, EndTime, StartDate, StartDate.AddYears(1), null, "America/New_York", ActorId, NowUtc);

        Assert.Equal(StartDate, rule.OccurrenceDate(0));
        Assert.Equal(StartDate.AddDays(3), rule.OccurrenceDate(1));
        Assert.Equal(StartDate.AddDays(6), rule.OccurrenceDate(2));
    }

    [Fact]
    public void OccurrenceDate_StepsWeeklyByIntervalValueWeeks()
    {
        var rule = new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 2,
            StartTime, EndTime, StartDate, StartDate.AddYears(1), null, "America/New_York", ActorId, NowUtc);

        Assert.Equal(StartDate, rule.OccurrenceDate(0));
        Assert.Equal(StartDate.AddDays(14), rule.OccurrenceDate(1));
    }

    [Fact]
    public void OccurrenceDate_StepsMonthlyByIntervalValueMonths()
    {
        var rule = new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Monthly, 1,
            StartTime, EndTime, StartDate, StartDate.AddYears(1), null, "America/New_York", ActorId, NowUtc);

        Assert.Equal(StartDate, rule.OccurrenceDate(0));
        Assert.Equal(StartDate.AddMonths(1), rule.OccurrenceDate(1));
        Assert.Equal(StartDate.AddMonths(2), rule.OccurrenceDate(2));
    }

    [Fact]
    public void OccurrenceDate_ThrowsOnNegativeIndex()
    {
        var rule = CreateValid();

        Assert.Throws<ArgumentOutOfRangeException>(() => rule.OccurrenceDate(-1));
    }

    // The last occurrence an OccurrenceCount-bound rule produces lands exactly
    // on ComputeImpliedEndDate's answer — same arithmetic, one implementation.
    [Fact]
    public void OccurrenceDate_OfTheLastIndexMatchesTheImpliedEndDate()
    {
        var rule = new RecurrenceRule(Guid.NewGuid(), ResourceId, UserId, RecurrenceFrequency.Weekly, 1,
            StartTime, EndTime, StartDate, null, occurrenceCount: 10, "America/New_York", ActorId, NowUtc);

        Assert.Equal(StartDate.AddDays(9 * 7), rule.OccurrenceDate(9));
    }

    [Fact]
    public void Cancel_SetsStatusCancelledAndTouchesAuditFields()
    {
        var rule = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddDays(1);

        rule.Cancel(actor, later);

        Assert.Equal(RecurrenceStatus.Cancelled, rule.Status);
        Assert.Equal(actor, rule.UpdatedByUserId);
        Assert.Equal(later, rule.UpdatedAtUtc);
    }
}
