namespace BookSpace.Domain.Enums;

// FR-3.1's "type", as a closed set. The four named values are the PRD's own
// examples of what a tenant publishes — "rooms, equipment, vehicles, lab slots"
// (PRD §1) — and Other is the escape hatch that keeps the rest honest: without
// it, an admin with a parking space has to miscategorise it, and a label people
// lie to is worse than a label with a catch-all.
//
// It was a free NVARCHAR(50) until 2026-09-04. That made it the only
// user-facing categorical column in this schema with no domain, so "Room",
// "room", "Meeting Room" and "Rooms" were four distinct values — sortable but
// not groupable, and not trustworthy enough to filter on.
//
// **It carries no behaviour, deliberately.** Nothing branches on it and no rule
// derives from it; in particular it does *not* constrain Capacity. Whether a
// resource is exclusive or pooled is what Capacity already says
// (docs/decisions/0005-capacity-semantics.md), and the two axes do not line up:
// a pool of three identical huddle rooms is a legitimately pooled Room, and
// "Van #2, the one with the tow bar" is a legitimately exclusive Vehicle. A
// capacity rule keyed off this label would block both. Owner's call,
// 2026-09-04.
public enum ResourceType
{
    Room,
    Equipment,
    Vehicle,
    LabSlot,
    Other
}
