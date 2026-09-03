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

## Known limitation, not yet resolved

The minimum-duration filter is applied to these intervals as they come out of
the sweep (Phase 5's answer 5). Because the sweep splits at every capacity
change, a booking that partially consumes capacity in the middle of an open span
leaves shorter intervals either side, and those can fall under the floor and
disappear — **even though a booking at a lower quantity spanning the whole run
would be accepted**.

This is inherent in the shape above: one figure per interval cannot also express
"at least one unit, for longer". It is recorded in `docs/wp3-plan.md` with a
worked example and pinned by a test
(`AShortBookingCanHideTimeThatIsStillBookableAtALowerQuantity`). Resolving it
means either a per-quantity response or moving the floor out of this query and
leaving it to WP-4's rejection — both contract changes, so both open.
