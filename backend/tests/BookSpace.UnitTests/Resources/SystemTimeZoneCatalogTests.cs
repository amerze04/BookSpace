using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Time;

namespace BookSpace.UnitTests.Resources;

// CLAUDE.md §4.3: the stored TimeZoneId must be an IANA id, because recurrence
// expansion and availability both run in .NET against it and SQL Server's
// AT TIME ZONE takes Windows ids that "will not match what .NET produces".
//
// These assert against the host's real timezone database, deliberately — the
// whole point of the class is what this machine will and will not resolve, and
// a fake would test nothing. They depend on ICU being present (it is, on
// Windows 10+ and any normal Linux container image); if a stripped image ever
// breaks them, the failure is a genuine deployment problem, not a flaky test.
public class SystemTimeZoneCatalogTests
{
    private readonly ITimeZoneCatalog _catalog = new SystemTimeZoneCatalog();

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Europe/Berlin")]
    [InlineData("Europe/Zagreb")]
    [InlineData("UTC")]
    public void AcceptsCanonicalIanaIds(string timeZoneId)
    {
        Assert.True(_catalog.IsKnownIanaId(timeZoneId));
    }

    // The interesting case. TimeZoneInfo on Windows resolves this happily, so a
    // plain "can we find it" check would accept it and put a value in the column
    // that the rest of the system cannot use.
    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("W. Europe Standard Time")]
    public void RejectsWindowsZoneIdsEvenThoughTimeZoneInfoResolvesThem(string windowsId)
    {
        Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out _));
        Assert.False(_catalog.IsKnownIanaId(windowsId));
    }

    // IANA ids are case-sensitive by convention, and refusing the wrong casing
    // is what keeps the stored value canonical.
    [Fact]
    public void RejectsNonCanonicalCasing()
    {
        Assert.False(_catalog.IsKnownIanaId("america/new_york"));
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("Not a zone at all")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsUnknownAndBlankValues(string timeZoneId)
    {
        Assert.False(_catalog.IsKnownIanaId(timeZoneId));
    }

    // ---- GetResourceTimeZone (WP-3 Phase 5) ----

    // The read side, and deliberately the more forgiving of the two: it only
    // requires that the stored id resolve. The conversion rules the returned
    // zone applies have their own tests in Availability/.
    [Fact]
    public void ResolvesAStoredIanaIdToItsZone()
    {
        var zone = _catalog.GetResourceTimeZone("America/New_York");

        Assert.Equal(
            new DateTime(2026, 9, 7, 13, 0, 0, DateTimeKind.Utc),
            zone.ToUtcEarliest(new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Unspecified)));
    }

    // Every id in Resources.TimeZoneId passed IsKnownIanaId on the way in, so an
    // id that will not resolve on the read path means the host's tzdata changed
    // under us — a 500, not something a client did.
    [Fact]
    public void Throws_WhenTheStoredIdDoesNotResolveAtAll()
    {
        Assert.Throws<TimeZoneNotFoundException>(() => _catalog.GetResourceTimeZone("Mars/Olympus"));
    }
}
