# 0022 — `ClosesAt = 23:59:59` means midnight

**Status:** Accepted
**Decided:** 2026-09-03, during WP-3 Phase 5 step 1
**Relates to:** [`0020`](0020-bookable-interval-semantics.md),
[`0021`](0021-daylight-saving-for-availability-ranges.md), FR-3.2

## Question

`CK_AvailabilityWindows_Window` requires `ClosesAt > OpensAt`, so **an
availability window cannot cross midnight**. A resource open from 22:00 to 02:00
has to be stored as two rows on consecutive weekdays:

```
Monday   22:00 → 23:59:59
Tuesday  00:00 → 02:00
```

`OpensAt` and `ClosesAt` are `time(0)`, so `24:00:00` is not expressible and
`23:59:59` is the largest value a window can close at. Taken literally, the pair
above leaves a **one-second hole at every midnight boundary** — and the two rows
never rejoin into the single overnight span they describe.

## Decision

**A `ClosesAt` of exactly `23:59:59` is read as the following midnight**, and
the rule applies **unconditionally** — not only when a next-day window exists to
join.

`AvailabilityWindowExpansion.ClosesAtEndOfDay` is the constant, and the
expansion promotes that window's end to `00:00:00` of the next local date.

## Why

**Why promote at all.** An admin who writes `23:59:59` means "until midnight",
because the schema gives them no other way to say it. Honouring the literal
value would be honouring a limitation of the column type rather than the
intent — and it would leave a one-second hole that is invisible at booking
granularity but is still a hole nobody asked for.

**Why unconditionally.** The alternative — promote only when a `00:00` window
follows on the next weekday — makes the *same stored row mean two different
things* depending on what its neighbour happens to be. Adding an unrelated
Tuesday window would then silently extend Monday's, and deleting one would
silently shorten it. A stored value should mean one thing.

**Why it makes the code simpler, not more complex.** Once promoted, the Monday
window's end and the Tuesday window's start are *the same instant*, so
`IntervalAlgebra.Merge` joins them as an ordinary touching pair. The overnight
case needs no special handling anywhere — which is the strongest argument that
the convention is in the right place.

## Cost, stated plainly

A window that closes at end-of-day with **nothing following it** gains one
second of availability — it runs to `00:00:00` rather than `23:59:59`. That is
one second at a boundary no booking granularity can express, and it is in the
direction the admin intended. Accepted.

A window closing at `23:59:00` — a minute short — is taken literally and does
**not** promote. Only the maximum expressible value carries the convention.

## Alternative considered

**Allow `ClosesAt <= OpensAt` to mean "crosses midnight"**, i.e. store
`22:00 → 02:00` as one row. Rejected: it means dropping
`CK_AvailabilityWindows_Window`, which is the tier-1 constraint that makes every
window an interval (CLAUDE.md §6), and every consumer would then have to know
that a window may or may not wrap. The schema doc is the source of truth
(CLAUDE.md §11) and this would change it for a presentational gain.

## Consequences

- Two windows are what an overnight schedule looks like in this system, and the
  API says so: `GET /resources/{id}` returns both rows, while
  `GET /resources/{id}/availability` returns the single joined span.
- The convention lives in exactly one place, `AvailabilityWindowExpansion`, and
  nothing downstream of it knows about `23:59:59`.
- Pinned by `AnOvernightScheduleStoredAsTwoRowsBecomesOneInterval`,
  `AnEndOfDayWindowOnTheLastDateInRangeStillRunsToMidnight` and
  `AWindowClosingJustShortOfEndOfDayIsTakenLiterally`, plus
  `Get_JoinsAnOvernightScheduleStoredAsTwoWindows` end to end.
