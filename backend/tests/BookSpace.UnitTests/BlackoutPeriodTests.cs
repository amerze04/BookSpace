using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

public class BlackoutPeriodTests
{
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2026, 12, 26, 0, 0, 0, DateTimeKind.Utc);

    private static BlackoutPeriod CreateValid() =>
        new(Guid.NewGuid(), ResourceId, Starts, Ends, "Public holiday", ActorId, NowUtc);

    [Fact]
    public void Constructor_Throws_WhenEndsAtUtcIsNotAfterStartsAtUtc()
    {
        Assert.Throws<ArgumentException>(() =>
            new BlackoutPeriod(Guid.NewGuid(), ResourceId, Starts, Starts, null, ActorId, NowUtc));
    }

    [Theory]
    [InlineData("2026-12-24T12:00:00Z", "2026-12-25T12:00:00Z", true)]   // overlaps start edge
    [InlineData("2026-12-25T12:00:00Z", "2026-12-26T12:00:00Z", true)]  // overlaps end edge
    [InlineData("2026-12-25T06:00:00Z", "2026-12-25T18:00:00Z", true)]  // fully inside
    [InlineData("2026-12-20T00:00:00Z", "2026-12-24T00:00:00Z", false)] // entirely before
    [InlineData("2026-12-26T00:00:00Z", "2026-12-27T00:00:00Z", false)] // entirely after
    public void Overlaps_ReturnsExpectedResult(string startsAt, string endsAt, bool expected)
    {
        var blackout = CreateValid();

        var result = blackout.Overlaps(DateTime.Parse(startsAt).ToUniversalTime(), DateTime.Parse(endsAt).ToUniversalTime());

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reschedule_UpdatesIntervalAndAuditFields()
    {
        var blackout = CreateValid();
        var newStart = Starts.AddDays(1);
        var newEnd = Ends.AddDays(1);
        var actor = Guid.NewGuid();
        var later = NowUtc.AddDays(1);

        blackout.Reschedule(newStart, newEnd, actor, later);

        Assert.Equal(newStart, blackout.StartsAtUtc);
        Assert.Equal(newEnd, blackout.EndsAtUtc);
        Assert.Equal(actor, blackout.UpdatedByUserId);
        Assert.Equal(later, blackout.UpdatedAtUtc);
    }

    [Fact]
    public void Reschedule_Throws_WhenEndsAtUtcIsNotAfterStartsAtUtc()
    {
        var blackout = CreateValid();

        Assert.Throws<ArgumentException>(() => blackout.Reschedule(Starts, Starts, ActorId, NowUtc));
    }
}
