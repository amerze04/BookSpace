# 0020 — A bookable slot is an interval carrying remaining capacity

**Status:** Accepted
**Decided:** 2026-08-28 by the repo owner (as WP-3 decision **D2**)
**Promoted to a numbered record:** 2026-09-03, when WP-3 Phase 5 implemented it
**Relates to:** [`0003`](0003-availability-timezone.md),
[`0005`](0005-capacity-semantics.md),
[`0015`](0015-api-contract-and-pagination.md),
[`0021`](0021-daylight-saving-for-availability-ranges.md),
[`0022`](0022-availability-window-midnight-convention.md)

## Question

`GET /resources/{id}/availability` has to answer "what can I book". What is the
shape of that answer?

## Decision

**A free interval carrying a `remainingCapacity` figure** — not a boolean
free/busy timeline, and not a grid of fixed-size slots.

```json
{ "startUtc": "2026-09-07T13:00:00Z", "endUtc": "2026-09-07T21:00:00Z", "remainingCapacity": 3 }
```

Three consequences follow, and all three are load-bearing:

1. **The interval is cut wherever the figure changes.** The number is a floor
   across the whole span, so a client can trust it at every instant inside it.
2. **Only bookable intervals are returned** — those with
   `remainingCapacity > 0`. See the narrowing below.
3. **`BookableInterval` refuses a remaining capacity of zero**, so the type's
   name cannot be false and no later step has to re-check.

## Why

Decision [`0005`](0005-capacity-semantics.md) makes `Capacity` a count of
*concurrent units*: a resource with capacity 4 supports four simultaneous
bookings, and `Bookings.Quantity` sums against it. A slot is therefore not
bookable-or-not — it has a number of units left.

- **A boolean free/busy shape would contradict the capacity model.** A booking of
  one unit against a capacity of four leaves the time genuinely open. Reporting
  it as busy would hide three quarters of the resource; reporting it as free
  would tell a client nothing about whether their own booking will fit.
- **A fixed grid would bake a granularity into the API the PRD never asks for.**
  Half-hour slots are a client's presentation choice, and a server that picks one
  forces every client to live with it — including the ones whose resources are
  booked by the day.

## Narrowed on 2026-09-03 (answer 1 of Phase 5's five shape questions)

D2's original wording said "free/busy intervals carrying `remainingCapacity`".
The endpoint returns **free intervals only** — there is no `kind` discriminator
and no `BlackedOut` or `Full` entry in the list.

D2's substance is intact: an interval with a number, not a fixed grid. But the
record should say **bookable intervals**, because the work-package task says
"return bookable slots" and the acceptance criterion says the query "excludes"
blackouts and bookings. A full timeline is API surface the PRD never asks for,
and it can be added later without breaking this shape.

The cost is stated where a client can act on it: a *gap* in the response means
"nothing bookable here" and does not say whether that is a blackout, a full
slot, or a closing time.

## Consequences

- `BookableInterval` (`BookSpace.Domain/Availability/`) is this decision as a
  type; `BookableIntervalDetail` is its wire form.
- The response carries `isArchived`, because an archived resource returns an
  empty list rather than an error (FR-3.5) and "empty" would otherwise have two
  meanings.
- WP-4's `CapacityExceeded` rejection has to agree with the figure this endpoint
  publishes. That is why the arithmetic lives in `CapacitySweep` in
  `BookSpace.Domain` rather than in the query handler — one implementation, two
  callers.

## Amendment — 2026-09-04: the answer is per-quantity

This record originally closed with a known limitation, and it is now fixed. The
fix changed the response's meaning, so it belongs here rather than in a note.

### The limitation

The minimum-duration filter was applied to intervals as they came out of the
sweep, and the sweep cut at every capacity change. So a booking that partially
consumed capacity in the middle of an open span left shorter intervals either
side, and those could fall under the floor and vanish — **even though a booking
at a lower quantity spanning the whole run would have been accepted**. On a
resource with a four-hour floor, one hour booked at 16:00 could hide the whole
afternoon.

### The root cause, which was not the filter

On a pooled resource, **"how long can I book?" has no single answer**, because
it depends on how many units you want:

```
Capacity 4, open 09:00–17:00, one booking 12:00–16:00 × 1 unit

want 1–3 units → 09:00–17:00   (8 hours)
want 4 units   → 09:00–12:00 and 16:00–17:00
```

The old shape answered with the finest partition — complete, but not a list of
bookable runs — and the filter then measured those fragments. Measuring the
wrong thing was the bug; the filter was only where it showed.

### The decision

**The endpoint takes an optional `quantity`, defaulting to 1.** Given a
quantity, the runs are determined: a segment that cannot hold that many units is
a wall, everything between two walls is one interval, and its
`remainingCapacity` is the **floor** across it. The minimum-duration filter then
measures runs a caller can actually take, and is correct by construction.

One is the honest default — the smallest legal booking
(`CK_Bookings_Quantity`), so the most permissive and most complete answer — and
on an exclusive resource (`Capacity = 1`, see
[`0005`](0005-capacity-semantics.md)'s amendment) it is the only possible value,
so nothing changes for those resources at all.

The response echoes `quantity`, because the same range on the same resource
answers differently for a different one.

### What it costs

**A run hides the fact that part of it had more units free than the rest.** With
the data above and `quantity=1`, the answer is one interval carrying 3, not a
three-part breakdown showing 4 either side of the booking. That figure is still
a floor a client can trust for any sub-span, which is what keeps it safe; a
caller who needs the texture asks again with a higher quantity.

Accepted, because the alternative — the finest partition, with the filter
measuring runs — would hand every client the job of joining the pieces before it
could answer the only question a booker actually has.

### Not resolved by this

The response still carries no `kind`, so a **gap** in the list means "nothing
bookable here" without saying whether that is a blackout, a wall, or a closing
time. That is answer 1's narrowing above, and it stands.
