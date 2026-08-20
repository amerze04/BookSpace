# 0008 — DST spring-forward policy for recurring occurrences

## Status
Decided — prompted by mentor ERD feedback (`docs/Amer-ERD-Feedback.docx`):
"the 02:30 spring-forward DST case — your model supports solving it — what's
the policy?"

## Context
A `RecurrenceRule`'s `LocalStartTime`/`LocalEndTime` are wall-clock times in
`TimeZoneId` (CLAUDE.md §4.3). On the day a timezone springs forward, some
local times (e.g. 02:00–03:00 in most US zones) don't exist — there is no
valid UTC instant for a local time of 02:30 that day. Something has to give
for any occurrence whose `LocalStartTime` falls in that gap.

This decision only covers the spring-forward (nonexistent-time) case, which
is what the mentor asked about. The fall-back case (an ambiguous local time
that occurs twice) is a separate, still-open question — see Notes.

## Decision
The occurrence is **skipped** — no `Booking` row is created for that date.
The affected user is told twice:
1. **Immediately, synchronously**, as part of the series-creation response
   (Decision #7's best-effort creation already reports skipped occurrences
   and why — DST is just one of the possible reasons).
2. **Again, 14 days before the occurrence's original date**, by email, in
   case the user has forgotten by the time it would have happened —
   creation can be months before a mid-series DST transition. This reuses
   the **existing** Reminder dispatch job (CLAUDE.md §7); no new background
   job. `NotificationKind.RecurrenceOccurrenceSkipped` is a new kind on the
   existing `Notifications` table.

No new "reschedule this occurrence" feature is introduced. If the user
wants a booking on that date, they create a normal one-time booking
themselves, same as for any other date.

### Why this needed a schema change
`Notifications.BookingId` was `NOT NULL`. A skipped occurrence has no
`Booking` to reference — that's the entire point. `Notifications.BookingId`
is now nullable, and `RecurrenceRuleId` + `OccurrenceDate` were added as an
alternate anchor, used only for this kind. `CK_Notifications_HasContext`
guarantees a row always points at exactly one of the two anchors, never
neither. See `docs/bookspace-schema-v2.sql` and `Notification.cs` for the
resulting dual-anchor shape.

### Why no new idempotency mechanism was needed
The reminder row is created exactly once, synchronously, as a side effect
of series creation — not by a job that repeatedly scans for "did I already
do this." That's the same guarantee every other `Notification` kind already
relies on. `UQ_Notifications_Once` was still widened to include
`RecurrenceRuleId`/`OccurrenceDate` (cheap, and gives this kind real
DB-level duplicate protection too, consistent with how the rest of the
system leans on constraints over trust — FR-9.4/AC-6).

## Consequences
- `Notifications.BookingId` nullable; `RecurrenceRuleId`, `OccurrenceDate`
  added; `CK_Notifications_HasContext` added; `UQ_Notifications_Once`
  widened; `RecurrenceOccurrenceSkipped` added to `CK_Notifications_Kind`.
- The Reminder dispatch job's email-composition logic needs a branch on
  `Kind`: join through `BookingId` normally, or through
  `RecurrenceRuleId` + `OccurrenceDate` for this one kind.
- 14 days is a fixed application constant, not a per-org setting like
  `ReminderLeadMinutes` — this is a rare edge case, not judged worth a new
  tenant-configurable column.
- **Open follow-up, not yet decided**: the fall-back (clocks-go-back)
  ambiguous-time case. A local time that occurs twice doesn't have the
  "nothing to create" problem spring-forward has — some default UTC offset
  choice (first vs. second occurrence) would need to be picked instead.
  Flag before recurrence expansion is implemented.
