namespace BookSpace.Application.Abstractions;

// Whether a string is a timezone id this system can actually resolve.
// Abstracted rather than calling TimeZoneInfo directly in a handler for two
// reasons: the answer depends on the host's timezone database (so it is
// infrastructure, not a rule), and a handler test should not be able to fail
// because a CI image ships a different tzdata.
//
// CLAUDE.md §4.3 requires IANA ids specifically — recurrence expansion and
// availability both run in .NET against the rule's IANA TimeZoneId, and SQL
// Server's AT TIME ZONE takes Windows ids that "will not match what .NET
// produces". So this deliberately answers a narrower question than "can
// TimeZoneInfo find it": on Windows, TimeZoneInfo resolves Windows ids too, and
// accepting one here would put a value in the column that the rest of the
// system cannot use.
public interface ITimeZoneCatalog
{
    bool IsKnownIanaId(string timeZoneId);
}
