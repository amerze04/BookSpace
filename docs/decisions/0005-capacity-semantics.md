# 0005 — Capacity: concurrent bookings or seats within one booking

## Status
Decided — formalises what the schema already implemented

## Context
Added during schema design, not from PRD §13 directly. FR-4.2 and FR-4.3 can
be read either as "Capacity = number of simultaneous bookings a resource
allows" or "Capacity = seats available within a single exclusive booking."

## Decision
`Resources.Capacity` means **concurrent units** the resource supports at
any instant. `Bookings.Quantity` is the number of units a given booking
consumes, and `dbo.CreateBooking` / `dbo.ApproveBooking` sum `Quantity`
across all temporally-overlapping `Confirmed`/`Pending` bookings for a
resource and reject if the sum would exceed `Resources.Capacity`
(`docs/bookspace-schema-v2.sql`, `CK_Bookings_Quantity`, and the
tier-2 locking protocol in CLAUDE.md §6). This is not "seats within one
exclusive booking" — a single resource can host multiple simultaneous,
independent bookings as long as their summed `Quantity` stays within
`Capacity`.

## Consequences
- No schema change — this decision only documents what
  `Resources.Capacity` + `Bookings.Quantity` + `CK_Resources_Capacity` /
  `CK_Bookings_Quantity` already implement.
- Domain-level `Booking` and `Resource` entities should carry this
  interpretation in their doc comments so future readers don't re-litigate
  it from the property names alone.

---

## Amendment — 2026-09-04: the exclusive/pooled reading, spelled out

Raised by the repo owner, who asked whether `ResourceType` should constrain
`Capacity` — "a room can only be capacity 1, but five vehicles can all be booked
at once". Investigating it showed the gap was in *this record*, not in the
schema.

The decision above rules the wrong reading **out** ("not seats within one
exclusive booking") without spelling the right one **in**. So here it is:

| | what the resource is | `Capacity` | a booking's `Quantity` |
|---|---|---|---|
| **Exclusive** | one physical thing | `1` | always `1` |
| **Pooled** | N interchangeable things | `N` | `1`…`N` |

**`Capacity = 1` *is* exclusivity.** There is no missing concept and nothing to
add: a resource with capacity 1 admits one booking at a time, which is exactly
what a specific meeting room or a single 3D printer needs. A capacity of 5 means
five interchangeable units — "pool cars", "loaner laptops", "lab bench
stations" — and a booking says how many of them it takes.

### Evidence that this needed writing down

The seed data had it wrong, in the dataset the whole project demos from:

```
Conference Room A   Capacity = 8     -- eight simultaneous bookings of one room
3D Printer          Capacity = 1
```

That `8` was written meaning *seats*, which is the reading this record exists to
forbid — and it is backwards besides, since the room is the exclusive resource
and equipment is the more plausible pool. Corrected to `1` on 2026-09-04.

### Why `ResourceType` does *not* constrain `Capacity`

Considered and rejected (owner's call, 2026-09-04). Three reasons:

1. **The categories do not line up with the booking models.** A "Room" is
   legitimately pooled when it is *"Huddle rooms — 3 identical"* or a hot-desk
   zone; a "Vehicle" is legitimately exclusive when it is *"Van #2, the one with
   the tow bar"*. A rule keyed off the label would block real setups and push
   admins into miscategorising things to get the capacity they need — making the
   label less trustworthy, not more.
2. **The PRD gives the type no behaviour.** FR-3.1 lists it beside capacity,
   timezone and description, as an attribute. Deriving a rule from it would be
   inventing a requirement (CLAUDE.md §11).
3. It would only catch a data-entry error, which is what a wrong capacity is —
   the same class as a wrong timezone, and the system cannot know the room is
   one room.

`ResourceType` did become an enum with a `CHECK` the same day, for consistency
with CLAUDE.md §5 — but as a *label*, carrying no rule. See
`CK_Resources_ResourceType` and `BookSpace.Domain/Enums/ResourceType.cs`.

### Where this reading now bites

`GET /resources/{id}/availability` takes a `quantity` parameter precisely
because of the pooled case: on a pooled resource the longest bookable run
depends on how many units the caller wants, so there is no single answer without
it (decision [`0020`](0020-bookable-interval-semantics.md)). On an exclusive
resource the parameter can only be 1 and the question does not arise.
