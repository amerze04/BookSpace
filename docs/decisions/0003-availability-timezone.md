# 0003 — Availability expressed in resource or booker timezone

## Status
Decided (2026-08-19) — formalises what the schema already implied

## Context
PRD §13 leaves open whether `AvailabilityWindows` (open hours) are
interpreted in the resource's timezone or the booking user's timezone.
Relevant to FR-6.1–FR-6.3 (recurrence/timezone handling).

## Decision
Availability is expressed in the **resource's** timezone
(`Resources.TimeZoneId`), not the booker's. `AvailabilityWindows.OpensAt` /
`ClosesAt` are resource-local wall-clock times (`docs/bookspace-schema-v2.sql`
already documents this on the table: "resource-local wall clock (FR-6.3)").
A booker in a different timezone sees availability converted for display,
but the stored and evaluated values are always resource-local.

## Consequences
- No schema change — this decision only makes explicit what the schema
  comment already encoded.
- The Application/UI layer is responsible for converting resource-local
  availability to the viewing user's timezone for display; the Domain layer
  never needs the booker's timezone to evaluate availability.
