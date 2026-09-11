# 0026 — Widening the Notifications anchor for a series-level notification

## Status
Decided and implemented 2026-09-09, WP-5 Phase 2.

## Context
Decision `0008` gave `Notifications` a second anchor — `RecurrenceRuleId` +
`OccurrenceDate` — for `RecurrenceOccurrenceSkipped`, the one kind with no
`Booking` to point at. `CK_Notifications_HasContext` was written for exactly
that one case: "`BookingId`, or `RecurrenceRuleId` **and** `OccurrenceDate`
together."

WP-5 Phase 2's whole-series cancel needs a notification kind that is about the
*series*, not any one occurrence's date — the owner's answer to the phase's
shape question 3 was one summary notification for the whole cancellation,
not one per occurrence. `SeriesCancelled` has a `RecurrenceRuleId` to anchor
to and no single `OccurrenceDate` that would be true of it; `0008`'s
constraint would refuse it.

## Decision
Widen `CK_Notifications_HasContext` from "`BookingId`, or `RecurrenceRuleId`
**and** `OccurrenceDate`" to "`BookingId`, or `RecurrenceRuleId` **alone**" —
`OccurrenceDate` becomes optional whenever `RecurrenceRuleId` is set, rather
than required alongside it. This is a strict widening: every row that
satisfied the old constraint still satisfies this one, so no existing data or
code path is affected. `RecurrenceOccurrenceSkipped` keeps setting both
columns; `SeriesCancelled` sets only `RecurrenceRuleId`.

`NotificationKind` gains `SeriesCancelled`, added to `CK_Notifications_Kind`
in the same migration (`WidenNotificationsRecurrenceAnchor`), and
`Notification` gains `ForSeriesCancelled(id, recurrenceRuleId, recipientUserId,
sendAtUtc, createdByUserId, nowUtc)` alongside `ForBooking` and
`ForSkippedOccurrence`.

## Consequences
- `UQ_Notifications_Once` — `(BookingId, RecurrenceRuleId, OccurrenceDate,
  RecipientUserId, Kind)` — needed no change. SQL Server's unique index
  treats two `NULL`s in the same position as a duplicate for uniqueness
  purposes, so a second `SeriesCancelled` row for the same rule and recipient
  (both with `OccurrenceDate IS NULL`) still collides correctly; there is no
  legitimate reason to send it twice anyway, since a series can only be
  cancelled once (`RecurrenceRule.CanBeCancelled`).
- The migration is a strict widening of a `CHECK` constraint, so its `Down`
  is a strict narrowing back to `0008`'s original text — safe only because no
  `SeriesCancelled` row can exist once it runs, which is guaranteed by
  `CK_Notifications_Kind`'s own `Down` removing the kind in the same
  migration.
- Verified by a real revert and re-apply against the dev database.
