# 0021 — Daylight saving inside an availability window is absorbed by the range

**Status:** Accepted
**Decided:** 2026-08-28 by the repo owner (as WP-3 decision **D3**)
**Promoted to a numbered record:** 2026-09-03, when WP-3 Phase 5 implemented it
**Relates to:** [`0003`](0003-availability-timezone.md),
[`0008`](0008-dst-spring-forward-policy.md),
[`0020`](0020-bookable-interval-semantics.md),
[`0022`](0022-availability-window-midnight-convention.md)

## Question

An `AvailabilityWindow` is a weekly rule in the resource's local wall-clock time
(FR-3.2, decision [`0003`](0003-availability-timezone.md)). Twice a year, that
local time misbehaves:

- **Clocks forward.** In `America/New_York` on 2026-03-08 the clock jumps
  `01:59:59 → 03:00:00`. Local `02:30` never happens.
- **Clocks back.** On 2026-11-01 it jumps `01:59:59 → 01:00:00`. Local `01:30`
  happens twice.

What does a window covering those times mean?

## Decision

**Expand the window to the UTC interval that actually elapsed on that date.**
The local day is simply 23 or 25 hours long, and the window is however long it
really was. No policy choice, no skipping, no shifting.

Making that concrete needs two resolution rules, because a wall-clock time does
not always name exactly one instant:

| local time | rule |
|---|---|
| **missing** (in the clocks-forward gap) | resolves to **the transition instant** — the moment the gap closes |
| **ambiguous** (in the repeated hour) | resolves to the **earlier** instant for a window's *start*, and the **later** instant for its *end* |

An "all day" window (`00:00`–end of day) then expands to 23 hours on
2026-03-08 and 25 hours on 2026-11-01, which is the test that pins this.

## Why these rules and not `TimeZoneInfo`'s defaults

.NET gets both cases wrong for this purpose, which is why the rules had to be
written down rather than inherited:

- `TimeZoneInfo.ConvertTimeToUtc` **throws** on a local time inside the gap. A
  window an admin saved months ago must not make a read fail.
- For an ambiguous local time it **assumes standard time** — the *later* of the
  two instants — for both ends of an interval. Taking the later instant for the
  *start* quietly shortens the window by an hour on that one day, which is
  availability the resource really had.

Taking the earlier instant for a start and the later for an end maximises the
interval, which is what "the time that actually elapsed" means.

## Why this is easier than decision `0008`'s question

Worth recording, because the two look like the same problem and are not.

A **window is a range**, so it can absorb a missing or repeated hour by simply
being shorter or longer. An **occurrence is an instant**, and an instant has to
land *somewhere* — which is why a recurring booking at 02:30 on a
spring-forward date needed a documented skip policy
([`0008`](0008-dst-spring-forward-policy.md)) and a notification to the owner.

## What this does not resolve

**The clocks-back case for recurring booking occurrences stays open** — the one
remaining open item in CLAUDE.md §9. This record resolves ambiguity for
*ranges*; an occurrence whose local time happens twice still has to pick one
instant, and nothing here picks it. WP-4 owns that question.

## Consequences

- `IResourceTimeZone` (`BookSpace.Domain/Availability/`) declares the two rules
  as two methods, `ToUtcEarliest` and `ToUtcLatest`, so a caller states which end
  of an interval it is converting.
- `SystemResourceTimeZone` (`BookSpace.Infrastructure/Time/`) implements them
  against the host's tzdata. Finding the transition instant needed its own
  method: `TimeZoneInfo` does not expose a gap's start, and `GetUtcOffset` on an
  invalid local time returns the *pre*-transition offset, which reproduces the
  "shift it an hour later" behaviour this decision rejects. It walks forward one
  second at a time to the first local time the zone considers real — exact for
  `time(0)` inputs, bounded at four hours, and only ever run for a local time
  actually inside a gap.
- A window lying **entirely** inside the gap opens and closes at the same
  instant, so it contributes nothing for that date and is dropped rather than
  reported as a zero-length slot. Correct — those local times did not happen —
  and the same window is ordinary on every other date.
- Verified against real tzdata (`SystemResourceTimeZoneTests`,
  `AvailabilityWindowExpansionTests`), including `Australia/Lord_Howe`, whose gap
  is 30 minutes rather than an hour, so nothing can be hard-coded to a whole
  hour.
