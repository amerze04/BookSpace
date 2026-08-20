# 0001 — Recurring series vs. a newly-added blackout

## Status
Decided (2026-08-19)

## Context
PRD §13 leaves open what happens to a recurring series' occurrences when a
blackout is added over a resource that already has confirmed or pending
occurrences scheduled in that window. Relevant to FR-3.4 (blackouts),
FR-5.2 (occurrences are independently cancellable).

## Decision
Blackouts have **absolute priority** over recurring series. When a
`BlackoutPeriod` is created (or edited to a wider range), every `Booking`
occurrence it overlaps — regardless of `Status` (`Pending` or `Confirmed`) —
is transitioned to `Cancelled`. The booking's owner (`Bookings.UserId`) is
notified by email. The `RecurrenceRule` itself is not cancelled; only the
overlapping occurrences are. Future occurrences not yet materialised are
simply never created against the blacked-out window (tier 4 availability
check already excludes blackout time — see §6).

## Consequences
- `Booking.Cancel(...)` needs a reason/actor variant distinct from a
  user-initiated cancel, so `CancellationReason` can record "blackout" and
  `CancelledByUserId` can stay `NULL` (the actor is the blackout creator,
  already captured on `BlackoutPeriods.CreatedByUserId`, not the booking).
- This is an Application-layer orchestration (find overlapping bookings for
  a resource, cancel each, enqueue a `Notifications` row per booking) — not
  something a single `Booking` or `BlackoutPeriod` entity method can do
  alone, since it spans multiple aggregates. Deferred to the Application
  layer when blackout creation is implemented; out of scope for the Domain
  entity pass.
- No schema change beyond the standard audit columns (see CLAUDE.md §9 #5
  sibling decisions) — `Bookings.Status = 'Cancelled'` and
  `CancellationReason` already cover this case.
