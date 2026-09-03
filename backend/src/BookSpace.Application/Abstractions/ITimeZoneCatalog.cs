using BookSpace.Domain.Availability;

namespace BookSpace.Application.Abstractions;

// The host's timezone database, behind an interface. Abstracted rather than
// calling TimeZoneInfo directly in a handler for two reasons: the answers depend
// on the host's timezone database (so it is infrastructure, not a rule), and a
// handler test should not be able to fail because a CI image ships a different
// tzdata.
//
// CLAUDE.md §4.3 requires IANA ids specifically — recurrence expansion and
// availability both run in .NET against the rule's IANA TimeZoneId, and SQL
// Server's AT TIME ZONE takes Windows ids that "will not match what .NET
// produces". So IsKnownIanaId deliberately answers a narrower question than
// "can TimeZoneInfo find it": on Windows, TimeZoneInfo resolves Windows ids
// too, and accepting one here would put a value in the column that the rest of
// the system cannot use.
public interface ITimeZoneCatalog
{
    // The write gate: may this string be stored in Resources.TimeZoneId?
    bool IsKnownIanaId(string timeZoneId);

    // The read side (WP-3 Phase 5): the zone a resource's availability is
    // expressed in, resolved once so the Domain calculation can convert its
    // local windows to instants without knowing where the offsets came from.
    //
    // Deliberately more forgiving than IsKnownIanaId, which is the gate on what
    // gets *written*: this only requires that the stored id resolve at all.
    // Refusing to read a resource because its zone id is a tzdata alias rather
    // than the canonical spelling would take the resource offline for a reason
    // no member could act on.
    //
    // Throws TimeZoneNotFoundException if the id does not resolve. That is a
    // 500, and correctly so — every id in the column passed IsKnownIanaId on the
    // way in, so failure here means the host's timezone database changed under
    // us, which is not something a client did or can fix.
    IResourceTimeZone GetResourceTimeZone(string timeZoneId);
}
