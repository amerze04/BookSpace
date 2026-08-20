# 0002 — TenantAdmin cancelling another user's booking

## Status
Decided (2026-08-19)

## Context
PRD §13 leaves open whether a TenantAdmin can cancel a booking they don't
own, and how the affected user finds out. Relevant to FR-4.x (booking
lifecycle), FR-8.1 (notifications).

## Decision
A `TenantAdmin` **can** cancel any booking within their own tenant,
including bookings owned by other users. `Bookings.CancelledByUserId` is set
to the TenantAdmin's `UserId` (distinct from `Bookings.UserId`, the owner).
The affected user is notified by **email** — and per this same decision,
**all** `Notifications` rows in this system are delivered by email; there is
no in-app or SMS channel. This applies uniformly to every `Notifications.Kind`
(`Confirmed`, `Rejected`, `Cancelled`, `Reminder`, `ApprovalRequested`,
`NoShowReleased`), not just admin-initiated cancellations.

## Consequences
- No schema change: `Bookings.CancelledByUserId` already supports "someone
  other than the owner cancelled this," and the composite tenant FK
  (`FK_Bookings_Resources_SameOrg`) already prevents a TenantAdmin from
  touching another tenant's booking.
- Authorization (TenantAdmin role check before allowing cancellation of a
  booking they don't own) belongs in the Application layer, not the Domain
  entity — `Booking.Cancel(actorUserId, reason, nowUtc)` accepts any actor id
  and records it; it does not itself decide who is *allowed* to call it.
- `docs/bookspace-schema-v2.sql`'s `Notifications` table comment references
  this decision for the "email-only" channel note.
