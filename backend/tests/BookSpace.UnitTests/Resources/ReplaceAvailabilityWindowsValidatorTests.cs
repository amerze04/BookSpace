using BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 3, FR-3.2. Shape only — the overlap rule is a 409 from
// AvailabilityWindowRules and is covered next door, because a validator cannot
// name a field for "these two contradict each other".
public class ReplaceAvailabilityWindowsValidatorTests
{
    private static readonly ReplaceAvailabilityWindowsCommandRequestValidator Validator = new();

    private static ReplaceAvailabilityWindowsCommandRequest Request(
        params AvailabilityWindowCommandItem[] windows) =>
        new(Guid.NewGuid(), windows);

    private static AvailabilityWindowCommandItem Item(DayOfWeek weekday, TimeOnly opensAt, TimeOnly closesAt) =>
        new(weekday, opensAt, closesAt);

    [Fact]
    public void Accepts_AWellFormedWeeklySchedule()
    {
        var result = Validator.Validate(Request(
            Item(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
            Item(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(12, 30))));

        Assert.True(result.IsValid);
    }

    // An empty array is a legitimate schedule — "this resource opens at no time
    // at all" — so it must not be a validation failure. Clearing a schedule is a
    // thing an admin does; it just has to be stated rather than achieved by
    // omitting the field.
    [Fact]
    public void Accepts_AnEmptySchedule()
    {
        var result = Validator.Validate(Request());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_AMissingWindowsArray()
    {
        var result = Validator.Validate(
            new ReplaceAvailabilityWindowsCommandRequest(Guid.NewGuid(), null!));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ReplaceAvailabilityWindowsCommandRequest.Windows));
    }

    [Fact]
    public void Rejects_AnEmptyResourceId()
    {
        var result = Validator.Validate(new ReplaceAvailabilityWindowsCommandRequest(
            Guid.Empty,
            new[] { Item(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0)) }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ReplaceAvailabilityWindowsCommandRequest.ResourceId));
    }

    // CK_AvailabilityWindows_Window, surfaced as a 400 naming the window rather
    // than a 500 from the domain ArgumentException behind it.
    [Theory]
    [InlineData(17, 9)]
    [InlineData(9, 9)]
    public void Rejects_AWindowThatDoesNotCloseAfterItOpens(int opensHour, int closesHour)
    {
        var result = Validator.Validate(Request(
            Item(DayOfWeek.Monday, new TimeOnly(opensHour, 0), new TimeOnly(closesHour, 0))));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Windows[0].ClosesAt");
    }

    // CK_AvailabilityWindows_Weekday BETWEEN 0 AND 6.
    [Fact]
    public void Rejects_AWeekdayOutsideTheEnum()
    {
        var result = Validator.Validate(Request(
            Item((DayOfWeek)7, new TimeOnly(9, 0), new TimeOnly(17, 0))));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Windows[0].Weekday");
    }

    // Both columns are time(0), so a sub-second value would be silently rounded
    // on write and the response would then disagree with the row a client reads
    // back — the trap CLAUDE.md §4.3 records for IClock and datetime2(0).
    // Rejected rather than truncated, matching how an oversized pageSize is
    // rejected rather than clamped.
    [Fact]
    public void Rejects_TimesCarryingFractionalSeconds()
    {
        var result = Validator.Validate(Request(
            Item(DayOfWeek.Monday, new TimeOnly(9, 0, 0, 500), new TimeOnly(17, 0, 0, 250))));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Windows[0].OpensAt");
        Assert.Contains(result.Errors, e => e.PropertyName == "Windows[0].ClosesAt");
    }

    [Fact]
    public void Accepts_TimesCarryingWholeSeconds()
    {
        var result = Validator.Validate(Request(
            Item(DayOfWeek.Monday, new TimeOnly(9, 0, 30), new TimeOnly(17, 30, 15))));

        Assert.True(result.IsValid);
    }

    // The error names the offending index, not just "one of the windows is
    // wrong" — a weekly schedule can be twenty entries long.
    [Fact]
    public void ReportsTheIndexOfTheOffendingWindow()
    {
        var result = Validator.Validate(Request(
            Item(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
            Item(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
            Item(DayOfWeek.Wednesday, new TimeOnly(17, 0), new TimeOnly(9, 0))));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Windows[2].ClosesAt");
    }
}
