using BookSpace.Domain.Common;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

public class BlackoutPeriodTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2026, 12, 26, 0, 0, 0, DateTimeKind.Utc);

    private static BlackoutPeriod CreateValid() =>
        new(Guid.NewGuid(), OrgId, ResourceId, Starts, Ends, "Public holiday", ActorId, NowUtc);

    // WP-3 decision D1: a blackout carries its own OrgId so it falls inside
    // all three CLAUDE.md §4.2 isolation mechanisms.
    [Fact]
    public void Constructor_SetsOrgId()
    {
        var blackout = CreateValid();

        Assert.Equal(OrgId, blackout.OrgId);
        Assert.Equal(OrgId, ((ITenantOwned)blackout).OrgId);
    }

    [Fact]
    public void Constructor_Throws_WhenEndsAtUtcIsNotAfterStartsAtUtc()
    {
        Assert.Throws<ArgumentException>(() =>
            new BlackoutPeriod(Guid.NewGuid(), OrgId, ResourceId, Starts, Starts, null, ActorId, NowUtc));
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

    // Revise replaces the WP-1 Reschedule, which took the interval only and never
    // acquired a production caller. PUT is a full representation
    // (docs/decisions/0015), so the reason has to move with the interval.
    [Fact]
    public void Revise_UpdatesIntervalReasonAndAuditFields()
    {
        var blackout = CreateValid();
        var newStart = Starts.AddDays(1);
        var newEnd = Ends.AddDays(1);
        var actor = Guid.NewGuid();
        var later = NowUtc.AddDays(1);

        blackout.Revise(newStart, newEnd, "Deep clean", actor, later);

        Assert.Equal(newStart, blackout.StartsAtUtc);
        Assert.Equal(newEnd, blackout.EndsAtUtc);
        Assert.Equal("Deep clean", blackout.Reason);
        Assert.Equal(actor, blackout.UpdatedByUserId);
        Assert.Equal(later, blackout.UpdatedAtUtc);
    }

    // A full representation, so an omitted reason means cleared rather than
    // unchanged — otherwise a reason could never be removed once set.
    [Fact]
    public void Revise_ClearsTheReasonWhenNoneIsSupplied()
    {
        var blackout = CreateValid();
        blackout.Revise(Starts, Ends, "Boiler service", ActorId, NowUtc);

        blackout.Revise(Starts, Ends, null, ActorId, NowUtc);

        Assert.Null(blackout.Reason);
    }

    // CreatedAtUtc/CreatedByUserId are history, not state.
    [Fact]
    public void Revise_LeavesTheCreationAuditAlone()
    {
        var blackout = CreateValid();
        var createdAt = blackout.CreatedAtUtc;
        var createdBy = blackout.CreatedByUserId;

        blackout.Revise(Starts.AddDays(1), Ends.AddDays(1), null, Guid.NewGuid(), NowUtc.AddDays(1));

        Assert.Equal(createdAt, blackout.CreatedAtUtc);
        Assert.Equal(createdBy, blackout.CreatedByUserId);
    }

    [Fact]
    public void Revise_Throws_WhenEndsAtUtcIsNotAfterStartsAtUtc()
    {
        var blackout = CreateValid();

        Assert.Throws<ArgumentException>(() => blackout.Revise(Starts, Starts, null, ActorId, NowUtc));
    }
}
