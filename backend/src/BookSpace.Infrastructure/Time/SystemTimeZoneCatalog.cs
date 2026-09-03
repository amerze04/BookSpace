using BookSpace.Application.Abstractions;
using BookSpace.Domain.Availability;

namespace BookSpace.Infrastructure.Time;

// ITimeZoneCatalog against the host's timezone database.
//
// IsKnownIanaId does two checks, not one, and the second is the interesting one.
// TryFindSystemTimeZoneById on Windows resolves *both* IANA ids
// ("America/New_York", via ICU) and Windows ids ("Eastern Standard Time"), so
// on its own it would happily accept a Windows id. CLAUDE.md §4.3 needs IANA
// specifically: recurrence expansion and availability run in .NET against this
// string, and the note there — that SQL Server's AT TIME ZONE takes Windows
// zone ids and "will not match what .NET produces" — is exactly the confusion
// storing the wrong flavour would create.
//
// TryConvertIanaIdToWindowsId is the discriminator: it succeeds only for a
// canonical IANA id. Verified on this machine (2026-08-31): "America/New_York",
// "Europe/Berlin" and "UTC" pass both checks; "Eastern Standard Time" resolves
// but is not IANA; "america/new_york" resolves case-insensitively but is not
// the canonical spelling, so it is refused too — which is what keeps the stored
// value canonical.
//
// Singleton: it holds no state, and TimeZoneInfo does its own caching.
internal sealed class SystemTimeZoneCatalog : ITimeZoneCatalog
{
    public bool IsKnownIanaId(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _)
            && TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out _);
    }

    // Resolution only — no IANA check, see the interface for why the read path
    // is deliberately the more forgiving of the two. FindSystemTimeZoneById's
    // own TimeZoneNotFoundException is the failure, unwrapped: it already says
    // which id could not be found, and there is no better answer to give.
    public IResourceTimeZone GetResourceTimeZone(string timeZoneId)
        => new SystemResourceTimeZone(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
}
