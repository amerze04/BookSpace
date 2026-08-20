# 0007 — Recurrence materialization: horizon, cap, and creation strategy

## Status
Decided — prompted by mentor ERD feedback (`docs/Amer-ERD-Feedback.docx`):
"State your recurrence horizon... how far ahead do you materialize, and
what job regenerates an open-ended series?"

## Context
`RecurrenceRules.RecurrenceRuleId` on `Bookings` means occurrences are
materialized as real `Bookings` rows, not expanded on the fly at read time.
`CK_RecurrenceRules_EndCondition` already requires exactly one of `EndDate`
or `OccurrenceCount`, so a series is never literally infinite — but an
`EndDate` far in the future, or a large `OccurrenceCount`, still made "how
far ahead do we materialize" a real question.

Two options were considered:
- **Rolling window + background top-up job**: materialize only a near-term
  window at creation; a periodic job extends it over time for still-`Active`
  rules.
- **Materialize everything at creation, with a cap on how long a series can
  run.**

## Decision
**Materialize everything at creation**, bounded by a hard cap: **a series
cannot run more than one calendar year past its own `StartDate`**. No
background top-up job exists or is needed.

The cap is enforced twice, because the two end-condition types need
different mechanisms:
- `EndDate`-bound rules: DB-level, `CK_RecurrenceRules_MaxSpan CHECK
  (EndDate IS NULL OR EndDate <= DATEADD(YEAR, 1, StartDate))`. Cheap and
  exact.
- `OccurrenceCount`-bound rules: Domain-level, in the `RecurrenceRule`
  constructor (`ComputeImpliedEndDate`), because computing the implied span
  correctly for `Monthly` recurrence (variable month length) isn't a clean
  single SQL expression that also works for `Daily`/`Weekly`. This still
  counts as CLAUDE.md §6 tier 4 ("duration limits" is explicitly a tier-4
  example there), and it's real, deterministic domain logic — no I/O
  needed to compute it.

**Creation is best-effort, not atomic.** If an individual occurrence within
the (now fully-known, ≤1-year) series conflicts with a blackout, exceeds
capacity, or falls in a DST spring-forward gap (Decision #8), that one
occurrence is skipped and the rest of the series is still created. The
series-creation response reports which occurrences were skipped and why.
Chosen over all-or-nothing because, per the reasoning that also applies to
DST: the user couldn't have prevented most of these conflicts (a blackout
added after the fact, a resource's capacity contended by someone else) by
retrying the whole request, and losing 350 valid occurrences because 1 of
them hit a conflict would be worse than reporting the 1 exception.

## Consequences
- No `MaterializedThroughUtc` (or similar) tracking column — not needed,
  since there's no rolling window to bookmark. The series is complete (up to
  best-effort exceptions) the moment `dbo.CreateBooking` finishes running
  for each occurrence.
- No new background job in CLAUDE.md §7 — still 3 jobs.
- A 1-year daily series is ~365 `dbo.CreateBooking` calls in one create-series
  operation. Acceptable for this domain (internal org resource booking, not
  high-volume public scheduling); revisit if that assumption changes.
- `RecurrenceRule`'s own span-cap check (`ComputeImpliedEndDate`) is the
  same calculation full occurrence-expansion will need anyway (last
  occurrence = `IntervalValue * (OccurrenceCount - 1)` steps after
  `StartDate`), so it isn't throwaway logic.
