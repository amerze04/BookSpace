# 0004 — No-show definition, determination, and grace period

## Status
Decided (2026-08-19)

## Context
PRD §13 leaves open what constitutes a no-show, who/what determines it, and
what the grace period is. Relevant to FR-9.1 (no-show release job), AC-1
(capacity correctness — a no-show must release its held capacity).

## Decision
A booking is a **no-show** when its `Status = 'Confirmed'`,
`CheckedInAtUtc IS NULL`, and the current time is past
`Bookings.StartsAtUtc + Organizations.NoShowGraceMinutes` (org-level
setting, already present on `Organizations.NoShowGraceMinutes`). The
determination is made by the **no-show release background job** (§7 of
CLAUDE.md) — not a person — which flips `Status` to `'NoShow'`, releasing
the resource's held capacity for that slot. Because the actor is a system
job, not a human, `Bookings.UpdatedByUserId` is set to `NULL` on this
transition (see the audit-trail convention note in
`docs/bookspace-schema-v2.sql`).

## Consequences
- No schema change: `Bookings.CheckedInAtUtc`, `Status`, and
  `Organizations.NoShowGraceMinutes` already fully support this. This
  decision only fixes the exact predicate.
- The Domain layer can expose a pure, testable predicate —
  `Booking.IsNoShow(DateTime nowUtc, int graceMinutes)` — since it needs no
  I/O: just the booking's own `Status`/`StartsAtUtc`/`CheckedInAtUtc` and the
  org's configured grace period passed in. The job itself (querying,
  updating, notifying) is Infrastructure/Application, not Domain.
- `IX_Bookings_NoShowSweep` (already indexed on `Status = 'Confirmed' AND
  CheckedInAtUtc IS NULL`) is the job's query path; StartsAtUtc + grace is
  evaluated in application code against that filtered set, consistent with
  CLAUDE.md §7's `UPDLOCK, READPAST` claiming pattern.
