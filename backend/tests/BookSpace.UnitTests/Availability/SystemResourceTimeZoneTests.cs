using BookSpace.Domain.Availability;
using BookSpace.Infrastructure.Time;

namespace BookSpace.UnitTests.Availability;

// The two resolution rules WP-3 decision D3 needs, which TimeZoneInfo's own
// defaults get wrong: it throws on a local time inside a clocks-forward gap and
// assumes standard time — the later instant — for an ambiguous one.
//
// Asserted against the host's real timezone database, deliberately and for the
// same reason SystemTimeZoneCatalogTests does: the class exists to interpret
// this machine's tzdata, and a hand-built zone would prove only that the
// arithmetic matches itself. The transitions used are the published 2026 ones
// for each zone; if a tzdata update ever moves them these fail loudly, which is
// the correct outcome rather than a flaky test.
public class SystemResourceTimeZoneTests
{
    // 2026 transitions: New York springs forward 03-08 at 02:00 (-05:00 ->
    // -04:00) and falls back 11-01 at 02:00.
    private static readonly IResourceTimeZone NewYork =
        new SystemResourceTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static DateTime Local(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Unspecified);

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    [Fact]
    public void AnOrdinaryLocalTimeResolvesToOneInstant()
    {
        var local = Local(2026, 9, 7, 9, 0);

        Assert.Equal(Utc(2026, 9, 7, 13, 0), NewYork.ToUtcEarliest(local));
        Assert.Equal(Utc(2026, 9, 7, 13, 0), NewYork.ToUtcLatest(local));
    }

    // Standard time in the same zone, to prove the offset is read per date
    // rather than fixed.
    [Fact]
    public void TheOffsetFollowsTheDate()
    {
        var local = Local(2026, 1, 7, 9, 0);

        Assert.Equal(Utc(2026, 1, 7, 14, 0), NewYork.ToUtcEarliest(local));
    }

    // ---- Clocks forward: the local times in the gap never happened ----------

    // Every local time from 02:00:00 to 02:59:59 on 2026-03-08 maps onto the
    // instant the gap closes, 07:00Z, which is 03:00 local. Both ends of an
    // interval get the same answer because there is only one candidate.
    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(2, 30, 0)]
    [InlineData(2, 59, 59)]
    public void AMissingLocalTimeResolvesToTheTransitionInstant(int hour, int minute, int second)
    {
        var local = Local(2026, 3, 8, hour, minute, second);

        Assert.Equal(Utc(2026, 3, 8, 7, 0), NewYork.ToUtcEarliest(local));
        Assert.Equal(Utc(2026, 3, 8, 7, 0), NewYork.ToUtcLatest(local));
    }

    [Fact]
    public void TheLocalTimesAroundTheGapAreUnaffected()
    {
        Assert.Equal(Utc(2026, 3, 8, 6, 59, 59), NewYork.ToUtcEarliest(Local(2026, 3, 8, 1, 59, 59)));
        Assert.Equal(Utc(2026, 3, 8, 7, 0, 1), NewYork.ToUtcEarliest(Local(2026, 3, 8, 3, 0, 1)));
    }

    // A 30-minute gap rather than an hour, so the walk to the end of the gap
    // cannot be hard-coded to a whole hour. Lord Howe goes +10:30 -> +11:00 on
    // 2026-10-04 at 02:00, so the gap is 02:00-02:29:59 and closes at 15:30Z the
    // previous UTC day.
    [Fact]
    public void AMissingLocalTimeResolvesCorrectlyWhereTheGapIsHalfAnHour()
    {
        var lordHowe = new SystemResourceTimeZone(
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe"));

        Assert.Equal(Utc(2026, 10, 3, 15, 30), lordHowe.ToUtcEarliest(Local(2026, 10, 4, 2, 15)));
    }

    // ---- Clocks back: the local times in the repeated hour happened twice ---

    // 01:30 on 2026-11-01 occurs at 05:30Z (still -04:00) and again at 06:30Z
    // (-05:00). An interval's start takes the first, its end the second — which
    // is what makes that local day 25 hours long instead of losing an hour.
    [Fact]
    public void AnAmbiguousLocalTimeResolvesEarlyForAStartAndLateForAnEnd()
    {
        var local = Local(2026, 11, 1, 1, 30);

        Assert.Equal(Utc(2026, 11, 1, 5, 30), NewYork.ToUtcEarliest(local));
        Assert.Equal(Utc(2026, 11, 1, 6, 30), NewYork.ToUtcLatest(local));
    }

    // TimeZoneInfo's default is the later instant for both ends; this is the
    // half of the pair it would have got wrong.
    [Fact]
    public void TheEarlyResolutionDiffersFromTimeZoneInfosDefault()
    {
        var local = Local(2026, 11, 1, 1, 30);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(local, zone), NewYork.ToUtcLatest(local));
        Assert.NotEqual(TimeZoneInfo.ConvertTimeToUtc(local, zone), NewYork.ToUtcEarliest(local));
    }

    // ---- Kind ---------------------------------------------------------------

    // A wall clock is not an instant. A UTC-Kind value arriving here means the
    // caller already converted, and converting twice silently is exactly what
    // CLAUDE.md §4.3's Kind conventions exist to stop.
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void ADateTimeThatIsNotAWallClockIsRefused(DateTimeKind kind)
    {
        var notAWallClock = DateTime.SpecifyKind(Local(2026, 9, 7, 9, 0), kind);

        Assert.Throws<ArgumentException>(() => NewYork.ToUtcEarliest(notAWallClock));
        Assert.Throws<ArgumentException>(() => NewYork.ToUtcLatest(notAWallClock));
    }

    [Fact]
    public void EveryResolutionIsStampedUtc()
    {
        Assert.Equal(DateTimeKind.Utc, NewYork.ToUtcEarliest(Local(2026, 9, 7, 9, 0)).Kind);
        Assert.Equal(DateTimeKind.Utc, NewYork.ToUtcLatest(Local(2026, 11, 1, 1, 30)).Kind);
        Assert.Equal(DateTimeKind.Utc, NewYork.ToUtcEarliest(Local(2026, 3, 8, 2, 30)).Kind);
    }

    // A zone with no DST rules at all has to come out of the same code path
    // unremarkably.
    [Fact]
    public void AZoneWithoutDaylightSavingResolvesDirectly()
    {
        var utc = new SystemResourceTimeZone(TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 3, 8, 2, 30), utc.ToUtcEarliest(Local(2026, 3, 8, 2, 30)));
        Assert.Equal(Utc(2026, 3, 8, 2, 30), utc.ToUtcLatest(Local(2026, 3, 8, 2, 30)));
    }

    // ---- ToLocal (WP-4 Phase 1a) -------------------------------------------

    // The offset actually in force at that instant, either side of the same
    // transitions the methods above have to reason about — EDT in September,
    // EST in January.
    [Theory]
    [InlineData(2026, 9, 7, 13, 0, 9, 0)]   // 13:00Z -> 09:00 EDT (UTC-4)
    [InlineData(2026, 1, 7, 13, 0, 8, 0)]   // 13:00Z -> 08:00 EST (UTC-5)
    public void ToLocalAppliesTheOffsetInForceAtThatInstant(
        int year, int month, int day, int utcHour, int utcMinute, int localHour, int localMinute)
    {
        var local = NewYork.ToLocal(Utc(year, month, day, utcHour, utcMinute));

        Assert.Equal(Local(year, month, day, localHour, localMinute), local);
    }

    // A wall clock is Unspecified, and this is the direction that has to produce
    // one — ToUtcEarliest/ToUtcLatest refuse anything else.
    [Fact]
    public void ToLocalReturnsAWallClock()
    {
        Assert.Equal(DateTimeKind.Unspecified, NewYork.ToLocal(Utc(2026, 9, 7, 13, 0)).Kind);
    }

    // The repeated hour is the case worth stating: 05:30Z and 06:30Z on
    // clocks-back day are two different instants that name the *same* wall
    // clock, 01:30. That is not an ambiguity in this direction — each instant
    // still has exactly one answer — which is why ToLocal needs no
    // earliest/latest pair.
    [Fact]
    public void TwoInstantsInTheRepeatedHourNameTheSameWallClock()
    {
        var earlier = NewYork.ToLocal(Utc(2026, 11, 1, 5, 30));
        var later = NewYork.ToLocal(Utc(2026, 11, 1, 6, 30));

        Assert.Equal(Local(2026, 11, 1, 1, 30), earlier);
        Assert.Equal(Local(2026, 11, 1, 1, 30), later);
    }

    // Round trip through the gap: no local time of 02:30 exists on
    // clocks-forward day, and ToUtcEarliest resolves it to the transition
    // instant, whose wall clock is 03:00. Asserted so the two directions are
    // known to be consistent rather than assumed to be.
    [Fact]
    public void AnInstantResolvedOutOfTheGapNamesTheTransitionWallClock()
    {
        var transition = NewYork.ToUtcEarliest(Local(2026, 3, 8, 2, 30));

        Assert.Equal(Local(2026, 3, 8, 3, 0), NewYork.ToLocal(transition));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void ToLocalRefusesAnythingThatIsNotAnInstant(DateTimeKind kind)
    {
        var notAnInstant = DateTime.SpecifyKind(new DateTime(2026, 9, 7, 13, 0, 0), kind);

        Assert.Throws<ArgumentException>(() => NewYork.ToLocal(notAnInstant));
    }

    // ---- IsInvalidLocalTime (WP-5) ------------------------------------------

    [Fact]
    public void AnOrdinaryLocalTimeIsNotInvalid()
    {
        Assert.False(NewYork.IsInvalidLocalTime(Local(2026, 9, 7, 9, 0)));
    }

    // The whole point of the ambiguous (clocks-back) case: it names two real
    // instants, neither of which is "invalid" — only the gap is.
    [Fact]
    public void AnAmbiguousLocalTimeIsNotInvalid()
    {
        Assert.False(NewYork.IsInvalidLocalTime(Local(2026, 11, 1, 1, 30)));
    }

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(2, 30, 0)]
    [InlineData(2, 59, 59)]
    public void ALocalTimeInsideTheGapIsInvalid(int hour, int minute, int second)
    {
        Assert.True(NewYork.IsInvalidLocalTime(Local(2026, 3, 8, hour, minute, second)));
    }

    [Fact]
    public void TheLocalTimesAroundTheGapAreNotInvalid()
    {
        Assert.False(NewYork.IsInvalidLocalTime(Local(2026, 3, 8, 1, 59, 59)));
        Assert.False(NewYork.IsInvalidLocalTime(Local(2026, 3, 8, 3, 0, 1)));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void IsInvalidLocalTimeRefusesAnythingThatIsNotAWallClock(DateTimeKind kind)
    {
        var notAWallClock = DateTime.SpecifyKind(Local(2026, 9, 7, 9, 0), kind);

        Assert.Throws<ArgumentException>(() => NewYork.IsInvalidLocalTime(notAWallClock));
    }
}
