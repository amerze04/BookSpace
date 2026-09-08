# 0002 — TenantAdmin cancelling another user's booking

## Status
Decided (2026-08-19). **Amended 2026-09-08** when WP-4 Phase 2b implemented it —
see the amendment section below for the four mechanics the original left open.

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

---

## Amendment (2026-09-08) — what implementation had to settle

Implemented in WP-4 Phase 2b (`POST /bookings/{id}/cancel`). The original
decision answered *whether* a TenantAdmin may cancel someone else's booking and
*how the user finds out*, and deliberately left the mechanics open. Building it
forced four answers, none of which contradict the original.

### 1. The reach is a read filter, not a role check at the call site

"A TenantAdmin can cancel any booking within their own tenant" is implemented as
the **same owner filter the read endpoints use** — `BookingReadRules` resolves a
`BookingOwnerFilter`, and the repository applies it as a `WHERE` clause. A member
gets `Owner(theirId)`; a TenantAdmin gets `AnyOwner`.

Two consequences worth stating:

- **"May I see it" and "may I cancel it" cannot disagree**, because they are
  literally the same function. A booking a member cannot read is a booking they
  cannot cancel, and no second rule has to be kept in step.
- **A booking the caller may not cancel is never loaded**, so the refusal is a
  404 `BookingNotFound` produced by an absent row rather than by a comparison the
  handler remembers to make. Another member's booking, another tenant's booking
  and a nonexistent id are byte-identical — never a 403, which would confirm the
  booking exists and leak who is holding which resource (AC-4, applied within a
  tenant rather than across one).

The original's note that authorization "belongs in the Application layer, not the
Domain entity" is unchanged and is what this implements. `Booking.Cancel` still
accepts any actor id and records it without deciding who was allowed to call it.

It also required a new capability: **nothing in `BookSpace.Application` could
read a role** before this. `ICurrentUser` gained `IsInRole(Role)`, because this
distinction cannot be a policy on the action — the same route serves both actors
and the difference is in which rows are reachable, not in whether the route may
be called.

### 2. The cancellation window is `EndsAtUtc`, not `StartsAtUtc`

A booking can be cancelled while it is **in progress** and not after it has
**ended**. Decision `0019`'s `BlackoutPeriodElapsed` rule, reapplied: the room is
free from now on, which is the whole point of cancelling, whereas cancelling
something that already finished frees nothing and only rewrites history.

This matters more than it looks, and for the reason `Booking
.CanBeCancelledForBlackout` already records: **nothing in this system writes
`BookingStatus.Completed`**, so an attended meeting from last month is still
`Confirmed`. Status alone would let a member "cancel" it.

`Booking.CanBeCancelled(nowUtc)` states the rule and `Booking.Cancel` re-checks
it. Before Phase 2b `Cancel` guarded only the terminal statuses, even though
`ReasonCodes.BookingNotCancellable` had always described both halves — the
elapsed half was documented and unenforced.

### 3. A second cancellation is refused, not treated as idempotent

`BookingNotCancellable` (422), deliberately unlike `POST
/resources/{id}/archive`, which no-ops when the resource is already archived.
The difference is that a cancellation **has something to overwrite**:
`CancelledByUserId`, `CancelledAtUtc` and `CancellationReason`. A repeat would
quietly change the record of who called the meeting off — which is precisely the
record this decision exists to create. Archiving writes no actor, so it has
nothing to lose by being repeatable.

### 4. Self-cancellation enqueues no notification

The original says the affected user is notified by email, and that every
`Notifications` row is delivered by email. Both stand. What it did not say is
what happens when the affected user **is** the actor.

**No row is written when the booking's owner cancels their own booking.** The
requirement is that the *affected user* is told; emailing someone the news they
just made is noise, and the 200 response body has already told them. A
cancellation by anyone else — an admin acting on this decision's authority — does
enqueue one, addressed to the **owner** rather than to the actor, with
`Notifications.CreatedByUserId` recording the admin.

The condition is **actor vs. owner, not role**: an admin cancelling their own
booking gets no notification either. A role-based check would have got that case
wrong.

Settled by the repo owner, 2026-09-08.

## Consequences of the amendment

- `Bookings.CancelledByUserId` is now actually written by a person for the first
  time. The blackout cascade leaves it null on purpose
  (`Booking.CancelForBlackout`, decision `0001`), so the column now distinguishes
  three cases: null = a rule cancelled it, equal to `UserId` = the owner did,
  different from `UserId` = an admin did.
- `CancelBookingCommandResponse` carries `CancelledByUserId` and
  `CancelledAtUtc` as non-nullable, because this endpoint only ever runs with a
  real person behind it. The read detail keeps them nullable, since a
  blackout-cancelled booking has no actor.
- **No approval request is withdrawn.** Cancelling a `Pending` booking leaves its
  `ApprovalRequests` row at `Decision = 'Pending'`. WP-5 owns approvals and its
  approve/reject path has to check the booking's status anyway (AC-5 already
  requires re-checking availability at approval time); the stale-approval expiry
  job (§7) would otherwise reach it. Raised rather than decided here — see
  `docs/wp4-plan.md`.
