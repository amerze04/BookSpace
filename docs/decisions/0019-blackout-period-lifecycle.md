# 0019 — Blackout period lifecycle: full CRUD, hard delete, and a forwards-only cascade

## Status
Decided (2026-09-02) — settled by the repo owner during WP-3 Phase 4 planning,
implemented across Phase 4 steps 1 and 2.

## Context
FR-3.4 says only that "a TenantAdmin can define blackout periods on a resource;
blackouts override availability." It says nothing about editing or removing one,
and decision [`0001`](0001-blackout-vs-recurring-series.md) mentions editing
only in passing ("created, or edited to a wider range").

That left five questions Phase 4 could not answer from the PRD, and one that
Phase 4 surfaced from the code. They were put to the repo owner before any code
was written, because CLAUDE.md §11 forbids inventing a requirement.

`0001` also makes blackout creation unusually consequential for a CRUD
endpoint: a blackout has **absolute priority**, so writing one cancels every
booking it overlaps and notifies each owner. That is a write against three
tables, not an insert into one.

## Decision

### 1. Full CRUD, not create-only
`POST`, `GET` (paginated), `PUT` and `DELETE`. FR-3.4's literal wording would
have justified create-only, but an admin who fat-fingers a date range needs a
way to correct it, and the correction has to be expressible without a database
console.

`PUT` is a **full representation** (decision `0015`), so an omitted `Reason`
means cleared. That is why `BlackoutPeriod.Reschedule` — WP-1's interval-only
mutator, which never acquired a production caller — was **widened into
`Revise`** rather than kept alongside a second method. A `Reschedule` unable to
express the endpoint's payload would have been dead code with a live-looking
name, which is exactly the trap CLAUDE.md §12 already records against
`Resource.AddAvailabilityWindow`.

### 2. Overlapping blackouts on one resource are allowed
Deliberately the opposite of availability windows, which reject overlap with
`OverlappingAvailabilityWindow` (409). The asymmetry is in the data, not in
taste: two overlapping windows genuinely contradict each other about when a
resource is open, and a union would be a guess. Two overlapping blackouts do
not contradict anything — the union is still blacked out — so there is nothing
to disambiguate and no information to lose.

Accepted cost: an admin can accumulate redundant blackouts, and the list will
show them. Preferable to refusing a request that is, in substance, already
satisfied.

### 3. The cascade cancels `Pending` and `Confirmed` only, and never reaches the past
`0001` says "regardless of `Status`". In practice that can only mean `Pending`
and `Confirmed`: `Booking.Cancel` already refuses `Cancelled`, `Completed` and
`NoShow`, and those hold no claim on the resource.

The second half is the one worth recording, because it is **not** derivable
from the status alone. **Nothing in this system writes
`BookingStatus.Completed`** — there is no `Complete()` method and no job in
CLAUDE.md §7 that sets it — so a meeting that actually happened and was checked
into stays `Confirmed` indefinitely. Cancelling on status alone would therefore
let a blackout covering last month cancel attended meetings and stamp
`CancelledAtUtc` on history, which FR-3.5's premise (history is preserved as it
was) rules out.

So `Booking.CanBeCancelledForBlackout(nowUtc)` requires **both** a live status
and `EndsAtUtc > nowUtc`. A booking already in progress *is* cancellable: the
room is unusable from now on, so the meeting in it has to stop.

### 4. A blackout entirely in the past is refused; one that merely starts in the past is not
New reason code `BlackoutPeriodElapsed` (`RuleViolation` → 422), thrown when
`EndsAtUtc <= now`. Such a blackout blocks nothing — nothing can be booked into
a window that has passed — and its only reachable effect would have been the
cascade reaching backwards, which rule 3 already refuses. The result would be a
row that looks like an action and had none.

The test is on `EndsAtUtc` and deliberately **not** on `StartsAtUtc`. "The room
flooded this morning and is unusable until Friday" is the ordinary operational
case, and a `StartsAtUtc >= now` rule would also reject a request assembled a
few seconds ago over nothing but clock skew.

On edit, the check applies to the **new** interval only: a blackout created last
week for yesterday may be revised into the future.

### 5. `DELETE` is a real hard delete, and it un-cancels nothing
The first hard delete in the codebase. CLAUDE.md §4.5 ("nothing is deleted") is
scoped to users and resources, which are archived because their booking history
must stay readable (FR-3.5). Nothing hangs history off a blackout: the reason a
booking was cancelled is a **text snapshot** on the booking itself
(`Bookings.CancellationReason`, written by `BlackoutCascade`), not a foreign
key. So the audit trail survives the row's removal intact, and `DELETE` means
what a client expects it to mean.

What it does not mean is "undo". Deleting a blackout stops it blocking *future*
bookings; bookings it already cancelled stay cancelled. `Booking` has no
`Uncancel`, and inventing one would mean an owner is emailed that their meeting
is off and then finds it silently back on their calendar.

**Deliberately not idempotent**: a second `DELETE` of the same id is 404, not
204. The end state is the same either way, so 204 would be defensible — but this
endpoint cannot distinguish "already deleted" from "another tenant's id" (AC-4
forbids it), so a blanket 204 would silently accept a cross-tenant identifier.
Note the contrast with `POST /resources/{id}/archive`, which *is* idempotent:
there the row is still present to inspect.

### 6. The edit's cascade runs forwards only
`PUT` re-runs `0001`'s cascade over the **new** interval. `0001` names widening;
moving matters just as much, since a blackout moved from Tuesday to Wednesday
covers bookings the original never touched.

Narrowing or moving restores nothing, per rule 5's reasoning. The response lists
only what *this* request cancelled — bookings the previous interval had already
cancelled were reported when it happened, and repeating them would read as a
fresh cancellation of meetings that have been off the calendar for a week.

### 7. An archived resource accepts no blackout writes at all
`PUT` and `DELETE` both return `ResourceArchived` (422), matching `POST`.

This is the one rule here that is arguable, and it was chosen for consistency
rather than convenience: an admin might reasonably want to tidy up blackouts on
a resource they have just archived. "An archived resource accepts no writes" is
a rule an admin can hold in their head, and the alternative would make deletion
its single exception. The practical cost is nil — an archived resource takes no
bookings, so its blackouts block nothing.

## Consequences

- **`Booking` gains `CancelForBlackout` and `CanBeCancelledForBlackout`**, which
  is `0001`'s predicted "reason/actor variant distinct from a user-initiated
  cancel". `CancelledByUserId` stays null: a person did cause this — the admin
  who wrote the blackout — but `0001` records that actor on
  `BlackoutPeriods.CreatedByUserId`, and writing them onto the booking would
  claim the owner was overruled by someone acting on their booking.
  `UpdatedByUserId` stays null for the same reason decision `0004` leaves it
  null on a no-show: a rule drove the transition, not an edit.
- **One repository port spans three tables.** `IBlackoutPeriodRepository` owns
  the blackout, the overlapping-booking query, the notification inserts and
  `SaveChangesAsync`, because all of it must land or fail together. Splitting it
  across a blackout port and a booking port would have left correctness resting
  on the unstated fact that both resolve the same request-scoped `DbContext`.
- **No explicit transaction, and CLAUDE.md §5 is not violated.** One
  `SaveChangesAsync` covers the blackout, the cancellations and the
  notifications; `SaveChanges` is already transactional, so the
  `CreateExecutionStrategy().ExecuteAsync(...)` rule — which exists for callers
  who would otherwise reach for `BeginTransaction` — does not apply.
- **§4.1 is not violated either, and the reasoning is recorded at the call
  site.** Booking *creation and approval* must go through `dbo.CreateBooking` /
  `dbo.ApproveBooking` because those add demand against `Resources.Capacity`
  and need `UPDLOCK, HOLDLOCK` range locks to compare the sum of overlapping
  `Quantity` against it. A cancellation only ever *reduces* the units held at an
  instant, so it cannot produce an overbooking and there is nothing for the
  locking protocol to protect. The lost-update case is covered independently by
  `Bookings.RowVersion` (two admins blacking out overlapping ranges → one gets
  409). Written into `BlackoutCascade`'s header specifically so the next reader
  does not cite this file as precedent for a LINQ *insert* into `Bookings`,
  which none of the above permits.
- **`Notifications` gets its first writer.** One row per cancellation,
  `Kind = Cancelled`, anchored to `BookingId`, `SendAtUtc = now`. The dispatch
  job does not exist yet, which is the intended design (§7): the row is the
  deliverable and `UQ_Notifications_Once` is what makes the eventual send
  idempotent (AC-6).
- **A second reason code, `BlackoutPeriodNotFound`** (`NotFound` → 404),
  distinct from `ResourceNotFound` even though both are 404s, because they say
  different things about the same URL: the resource is wrong, or the resource is
  fine and the blackout is not. It collapses three cases — no such id, another
  tenant's id, and an id belonging to a different resource — since reporting the
  third separately would confirm the id exists (AC-4).
- **`ReasonCodes.BlackoutPeriod` is not thrown by this phase.** Despite the
  name it is a *booking* rejection (FR-4.5): the interval a client asked for is
  covered by a blackout. Its thrower arrives with `dbo.CreateBooking` in WP-4.
  CLAUDE.md §12 previously listed it as Phase 4's remaining thrower; that line
  is corrected.
- **Instants on the wire must carry a zone.** This feature is the first in the
  API to accept one. `DateTimeKind.Unspecified` (no `Z`, no offset) is refused
  with a 400 rather than interpreted, because the only available interpretation
  is the *server's* timezone — precisely the silent app/database disagreement
  §4.3 exists to prevent. An explicit offset is accepted and normalized to UTC.
  Sub-second values are refused too: the columns are `datetime2(0)`, which
  *rounds* on write, so the response would disagree with the row a client reads
  back.

## Alternatives considered

- **Soft-delete a blackout** (an `IsRemoved` flag), for symmetry with §4.5.
  Rejected: the flag would exist to preserve a row nothing references, and every
  read would need to filter it. §4.5's purpose is protecting history, and the
  history here lives on the booking.
- **Reject `StartsAtUtc` in the past.** Rejected as too strict — it would refuse
  the flooded-room case and lose to clock skew. See rule 4.
- **Restore bookings when a blackout is narrowed or deleted.** Rejected: a
  cancellation is a fact communicated to a person, so reversing it silently is
  worse than leaving it. If a booking should come back, the owner rebooks.
- **Reject overlapping blackouts, for symmetry with availability windows.**
  Rejected; see rule 2.
- **Return 204 for a repeated `DELETE`** (strict REST idempotence). Rejected
  because it cannot be done without also accepting cross-tenant ids; see rule 5.

## Notes

`Completed` having no writer (rule 3) is a **roadmap gap, not a Phase 4 bug**.
Phase 4 defends against it locally with the `EndsAtUtc > now` guard. The
suggested fix — fold the missing transition into §7's existing no-show release
job, splitting past `Confirmed` bookings on `CheckedInAtUtc` (null → `NoShow`,
non-null → `Completed`) rather than adding a fourth job — was agreed with the
repo owner on 2026-09-02 as WP-4's business, since WP-4 owns the booking
lifecycle and check-in is where the consequence lands. It needs `Booking` to
gain a `Complete()` method and an `IsComplete` predicate, mirroring the shape
decision `0004` established for no-shows, and note the two predicates differ:
`IsNoShow` measures grace from `StartsAtUtc`, whereas "completed" is about
`EndsAtUtc` having passed.
