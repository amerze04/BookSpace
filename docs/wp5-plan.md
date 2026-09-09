# WP-5 — Recurrence, Approvals & Time Correctness

Source: `docs/Work Packages - Week 4.pdf` (week 4, backend track), which carries
WP-4 and WP-5 together. **That PDF is the authority on scope** — the task list
in CLAUDE.md §12 is copied from it, and anything not in it is a gap to flag
rather than something to add on judgment (CLAUDE.md §11).

---

## Status

**In progress — Phases 1 and 2 done, 2026-09-09.** All seven shape questions
below were put to the repo owner on 2026-09-08, before any WP-5 code was
written — the same process WP-3 and WP-4 each went through, and the one this
document's own §6 asked for. Their answers are recorded in "Settled before
planning" and the architecture and phasing that follow are built on them. The
one genuinely open decision (the DST fall-back policy) is closed as `0024`,
the first thing this package decided.

**Test baseline: 978 unit + 430 integration tests pass, 0 failed.** `dotnet
build` is clean across the solution. `POST /recurrence-rules` and
`POST /recurrence-rules/{id}/cancel` both exist and work end to end, through a
real SQL Server, the first with a real `dbo.CreateBooking` call per occurrence.
Phase 2 also found and fixed a real pre-existing tenant-isolation gap —
`RecurrenceRules` had none at all — recorded as
[`0025`](decisions/0025-recurrence-rule-tenant-scoping.md); see Phase 2's entry
in §9 for the full story.

---

## 1. What WP-5 owes

Copied from the source doc via CLAUDE.md §12.

- Create recurring bookings (daily/weekly/monthly) with interval and end
  condition. FR-5.1.
- Make each occurrence independently viewable and cancellable. FR-5.2.
- Support cancelling one occurrence or the whole remaining series. FR-5.3.
- Surface collisions and blackout conflicts at creation — never drop them
  silently. FR-5.4.
- Implement the approval workflow: `Pending` → approve/reject → notify;
  **re-check availability at approval time**. FR-7.1–FR-7.5.
- Store all times as UTC; render in the correct local zone. FR-6.1.
- Define and implement DST-transition behaviour for recurring bookings. FR-6.2.

Acceptance criteria:

- A recurring series is created, and single occurrences and the whole series can
  each be cancelled.
- Conflicting occurrences are surfaced at creation.
- Approval re-checks availability, so approving a since-taken slot fails safely.
  **AC-5.**
- The DST edge case resolves per the documented policy with no crash or silent
  duplicate. **AC-3.**

---

## 2. What already exists that WP-5 builds on

Most of the hard groundwork is done. Confirmed against the actual code, not
just the header comments, before writing this plan.

| Piece | Where | Note |
|---|---|---|
| `RecurrenceRule` entity | `Domain/Entities/RecurrenceRule.cs` | frequency, interval, local start/end time, start/end date, occurrence count, IANA `TimeZoneId`, `Status`, `Cancel(actorUserId, nowUtc)` (unconditional today — see §7), `ComputeImpliedEndDate` |
| `RecurrenceStatus` | `Domain/Enums/` | `Active`, `Cancelled` — only two |
| `Bookings.RecurrenceRuleId` | schema + `NewBooking` | nullable, **always null today** — `CreateBookingCommandRequestHandler` passes it as `RecurrenceRuleId: null` explicitly, with a comment naming WP-5 |
| `dbo.CreateBooking` | `AddCreateBookingProcedure` migration | takes `@RecurrenceRuleId = NULL` as an optional parameter already; every occurrence goes through it unchanged |
| `IBookingRepository.CreateAsync` / `NewBooking` | `Application/Abstractions/IBookingRepository.cs` | the exact port a per-occurrence create reuses; `NewBooking.RecurrenceRuleId` just needs a real value |
| `ApprovalRequest` entity | `Domain/Entities/ApprovalRequest.cs` | `Decide(decision, decidedByUserId, nowUtc, note)`, `Expire(nowUtc)` — both already guard `Decision != Pending`; rows are **written** by WP-4's create path (`CreateBookingCommandRequestHandler.StageApprovalAsync`) |
| Approvers on a resource | WP-3 Phase 3 | `Resource.ApproverUserIds` (computed over an EF owned collection, `ResourceApprovers` table); eligibility rules in `0018` |
| `IResourceTimeZone` | `Domain/Availability/IResourceTimeZone.cs` | `ToUtcEarliest` / `ToUtcLatest` / `ToLocal`; `SystemResourceTimeZone` is the Infrastructure implementation, with the gap-walk and ambiguous-offset logic `0024` reuses as-is |
| `BookingEligibility` + `AvailabilityCalculator` | `Domain/Availability/` | "may this exact interval be booked, and why not" — the pure function every occurrence's pre-check calls, unchanged |
| `Notification.ForSkippedOccurrence` | `Domain/Entities/Notification.cs` | **already implemented**, not just declared — the dual-anchor factory for `RecurrenceOccurrenceSkipped` exists and is ready to call |
| `IUnitOfWork` | `Infrastructure/Persistence/UnitOfWork.cs` | the transactional boundary; **its delegate must be safe to run twice** (1205 retry) |
| `ICurrentUser.IsInRole(Role)` | `Application/Abstractions/ICurrentUser.cs` | first read in WP-4 Phase 2a; WP-5's approver reach reuses it for `Role.Approver` |
| `BookingOwnerFilter` / `BookingReadRules` / `BookingScope` | `Application/Features/Bookings/` | the "who may see whose bookings" machinery `GET /bookings` already has; the approver queue extends rather than replaces it (§5.3 below) |
| Seeded recurrence rule | `SeedData.cs` | one weekly standup per tenant (`Weekly`, interval 1, `StartDate` 2026-08-24, `EndDate` = `StartDate + 2y`), **no occurrences materialized** — the comment there still says why |
| Seeded bookings | `SeedData.cs` (WP-4 Phase 3) | three per tenant through `dbo.CreateBooking`, including one `Pending` on the approval-gated 3D Printer with its `ApprovalRequest` row — the approver queue has something to read on day one |

**What does not exist and WP-5 must build:** `dbo.ApproveBooking`, `Booking.Reject(...)`
(there is `Cancel`, `CancelForBlackout`, `CheckIn`, `MarkNoShow` — nothing for a
decision), `RecurrenceExpansion` (the application-layer wall-clock expansion
CLAUDE.md §4.3 requires), `IRecurrenceRuleRepository`, the approver queue's
resource-scoping, and every endpoint in §1.

---

## 3. The decisions WP-5 inherits, already settled

- [`0007`](decisions/0007-recurrence-materialization-horizon.md) — a series is
  **fully materialized at creation**, **best-effort per occurrence** (not
  atomic), capped at **two calendar years** past its own `StartDate`. No
  background top-up job. **Load-bearing for the create-series architecture in
  §5.1**: best-effort-per-occurrence means each occurrence gets its own
  `IUnitOfWork.ExecuteAsync`, never one transaction wrapping the whole series.
- [`0008`](decisions/0008-dst-spring-forward-policy.md) — an occurrence whose
  local time falls in a spring-forward gap is **skipped**, reported immediately
  in the creation response, and reported again 14 days ahead by email via the
  existing Reminder job. `Notification.ForSkippedOccurrence` already exists.
- [`0001`](decisions/0001-blackout-vs-recurring-series.md) — a blackout has
  **absolute priority** over a series: it cancels every occurrence it overlaps,
  at any status, and the owner is notified. Already implemented by
  `BlackoutCascade`, unchanged by WP-5 — it starts cancelling real series
  occurrences the moment they exist.
- [`0003`](decisions/0003-availability-timezone.md) — availability is expressed
  in the **resource's** timezone. **Consequence for series creation**: a
  `RecurrenceRule` expands in the resource's `TimeZoneId`, not a client-supplied
  one — see smaller call 1 in §5.1.
- [`0002`](decisions/0002-tenant-admin-cancellation.md), as amended by WP-4 —
  the cancellation window is `EndsAtUtc`, a TenantAdmin's reach is the same
  owner filter the reads use, a second cancellation is refused, and
  self-cancellation enqueues no notification. §5.2 applies all four per
  occurrence when cancelling a whole series.
- [`0023`](decisions/0023-booking-concurrency-strategy.md) — `dbo.ApproveBooking`
  inherits the concurrency design: same locks, same order, same index. §5.3
  works out *why* a re-check is needed even though a `Pending` booking already
  holds its capacity claim from creation — it is not a redundant formality.
- [`0024`](decisions/0024-dst-fallback-recurrence-policy.md) — **new, decided
  2026-09-08.** A recurring occurrence's ambiguous (fall-back) local time
  resolves to the **earlier** UTC instant, for both its start and its end —
  not `0021`'s start-earlier/end-later split, which is a *range's* rule. This
  was CLAUDE.md §9's last open item; it is now closed.

---

## 4. Loose ends WP-5 inherits from WP-4, and how this plan closes them

1. **A cancelled `Pending` booking keeps its `ApprovalRequests` row at
   `Pending`.** §5.3's approve/reject design closes this directly: both paths
   check `Booking.Status == Pending` before doing anything else, so a
   cancelled (or otherwise no-longer-Pending) booking is refused with a new
   `BookingNotPending` rather than silently approved.
2. **Nothing writes `BookingStatus.Completed`.** Unchanged by WP-5, still worth
   raising with the mentor; not in this package's scope.
3. **Read DTOs carry `UserId` but no owner name.** The approver queue is
   exactly the screen this bites — an approver needs to know who asked.
   Addressed in §5.3: the list/detail DTOs gain the booker's name, following
   `ApproverDetail`'s id-and-name-no-email precedent.
4. **`GET /bookings/{id}` carries no approval detail for a `Pending` booking.**
   Addressed in §5.3 — the detail DTO gains an approval section once
   `ApproveBooking`/`RejectBooking` exist to act on it.
5. **Reminder rows (FR-8.3) are written by nothing.** Still deferred to the
   notifications package; WP-5's source doc does not ask for reminders beyond
   the one `0008` already specifies (which is built).

---

## 5. Architecture

### 5.1 Creating a recurring series (FR-5.1, FR-5.4)

**New port:** `IRecurrenceRuleRepository` (`Add`, `SaveChangesAsync`) —
`RecurrenceRule` is a plain EF entity with no capacity claim of its own, so it
needs no procedure, matching `IBookingRepository`'s `AddApprovalRequest` /
`AddNotifications`.

**New pure function, `RecurrenceExpansion`, in `BookSpace.Domain`** (alongside
`BookingEligibility` — same reasoning: no EF, no clock beyond what is passed
in, and it is the one place a rule's occurrence dates and instants are computed,
so a second implementation would drift). Signature, shape not final text:

```csharp
public static IReadOnlyList<RecurrenceOccurrence> Expand(RecurrenceRule rule, IResourceTimeZone zone);

public sealed record RecurrenceOccurrence(
    DateOnly OccurrenceDate,
    RecurrenceOccurrenceOutcome Outcome,   // Instant | SkippedSpringForwardGap
    UtcInterval? Interval);                // null iff Outcome is the skip
```

Walks `StartDate` forward by `Frequency`/`IntervalValue` until `EndDate` (or
`OccurrenceCount`) is exhausted — the same stepping `RecurrenceRule
.ComputeImpliedEndDate` already does for the span cap, reused rather than
re-derived. For each date: resolve `LocalStartTime`/`LocalEndTime` to UTC via
`zone.ToUtcEarliest` for **both** ends per `0024`, unless
`zone`'s underlying `TimeZoneInfo.IsInvalidTime(localStart)` is true, in which
case the occurrence is `SkippedSpringForwardGap` per `0008` and no interval is
produced. (Testing the gap needs the zone's own predicate, not
`IResourceTimeZone` — `SystemResourceTimeZone` already exposes the wrapped
`TimeZoneInfo` internally; whether that needs a one-line addition to the
interface or can be inferred by catching the specific instant is a Phase 1
detail, not a design question.)

**The handler, in sequence** (`CreateRecurrenceSeriesCommandRequestHandler`,
mirroring `CreateBookingCommandRequestHandler`'s division of labour):

1. Load the resource (`ResourceNotFoundException` on a missing/cross-tenant id,
   AC-4), reject archived (`ResourceArchivedException`).
2. `resource.AllowsBookingDuration(LocalEndTime - LocalStartTime)` — one check,
   since every occurrence shares the same nominal duration
   (`BookingDurationOutOfRangeException`).
3. Construct the `RecurrenceRule` — its constructor already enforces the
   interval/end-condition/two-year-cap rules (`CK_RecurrenceRules_*`, decision
   `0007`), so nothing here re-derives them. `Add` it and `SaveChangesAsync`
   through `IRecurrenceRuleRepository` **before** any occurrence, since
   `Bookings.RecurrenceRuleId` is a real FK.
4. `RecurrenceExpansion.Expand(rule, zone)` — the full occurrence list, still
   in memory, nothing written yet.
5. **One up-front snapshot read** of blackout intervals and booked quantities
   across the whole series span (the same two `IAvailabilityRepository` calls
   the single-booking handler makes, just over a wider range) — cheap relative
   to up to ~730 occurrences each needing their own pair of queries, and safe
   to share across occurrences precisely because occurrences of **one** rule
   never overlap each other in time (a rule produces at most one occurrence per
   step), so an earlier occurrence in this same loop cannot change a later
   one's eligibility answer. It is advisory, exactly as the single-booking
   pre-check is — `dbo.CreateBooking` remains the authority per occurrence.
6. For each occurrence with a real `Interval` (i.e. not a spring-forward skip):
   - `BookingEligibility.Evaluate(...)` against the snapshot. A refusal here
     (`OutsideAvailability` / `BlackoutPeriod` / `SlotUnavailable` /
     `CapacityExceeded`) is recorded as **refused, with reason**, and
     `dbo.CreateBooking` is **not called** for it — the pre-check answer is
     confident enough to skip a doomed round trip, matching the single-booking
     handler's own reasoning for why it pre-checks at all.
   - Otherwise, its own `IUnitOfWork.ExecuteAsync` (never one for the whole
     series — see decision `0007` above): mint the occurrence's `Booking` id
     before entering it, call `dbo.CreateBooking` with this rule's id, stage the
     `ApprovalRequest` (if `RequiresApproval`) and `Notifications` exactly as
     WP-4 does, `SaveChangesAsync`. If the procedure itself refuses — the
     snapshot was stale, which can happen for a long series racing a genuinely
     concurrent booking — that occurrence is recorded as **refused** with the
     procedure's reason rather than aborting the remaining occurrences
     (decision `0007`'s best-effort guarantee, applied to a failure the
     pre-check did not catch).
   - A spring-forward skip is recorded as **skipped**, and a
     `Notification.ForSkippedOccurrence` is enqueued (14 days ahead,
     `SendAtUtc = occurrenceDate - 14d`, via the existing Reminder job — `0008`
     unchanged) in the same save as the *next* real occurrence's transaction,
     or its own trivial one if none follows — a Phase 1 detail.
7. Build the response from the three buckets: created (with `bookingId`),
   skipped (DST), refused (with reason code) — FR-5.4's "never drop silently"
   is the created/skipped/refused partition itself, not a side effect of it.

**The empty-series wire question (settled answer to shape question 2).** If
every occurrence is skipped or refused, the endpoint must not return 201 with
an empty `created` list — the owner's answer was that this is a 409/422.
Concretely: a new `NoOccurrencesCreatedException` (`ErrorKind.RuleViolation`,
422 — chosen over 409 because the common case is every occurrence landing
outside availability, which is a rule refusal, not a race; the rare case where
every occurrence loses a race is still describable as "nothing could be
booked") carrying the same per-occurrence breakdown as a successful response
would have. This needs one small, general mechanism `0016` does not yet have:
`AppException` currently has no way to attach structured data to a response
beyond the reason code (the one exception, `FluentValidation.ValidationException`,
is special-cased in `GlobalExceptionHandler` for per-field errors). Rather than
special-casing this one exception the same way, Phase 1 adds an optional
`IReadOnlyDictionary<string, object?> Extensions` to `AppException` (empty by
default) that `GlobalExceptionHandler` copies onto `ProblemDetails.Extensions`
generically — a small generalization of the existing special case, usable by
this exception and nothing forces it to be used by any other. **A smaller call
to make explicit when Phase 1 is scoped**, not a question for the owner: it
follows directly from the chosen wire shape and does not reopen `0016`'s
design.

**Smaller calls taken here, to flag rather than re-ask:**

1. **The series' timezone is always the resource's `TimeZoneId`**, never a
   client-supplied one, per `0003`. `RecurrenceRule`'s constructor still takes
   a `timeZoneId` parameter (used freely by `SeedData` today), but the
   command handler always passes `resource.TimeZoneId` — the endpoint's
   request DTO has no timezone field at all, so there is nothing for a client
   to get wrong.
2. **`Title` is shared by every occurrence in the series** — one field on the
   create-series request, copied onto every `NewBooking`, exactly as `Quantity`
   is. No per-occurrence override; nothing in FR-5.1 asks for one.
3. **The pre-check snapshot is taken once, not refreshed per occurrence.**
   Justified above (occurrences of one rule cannot overlap each other), and it
   is the same trade the single-booking handler already makes at a smaller
   scale — the procedure is what actually decides.

### 5.2 Cancelling a whole series (FR-5.2, FR-5.3)

**Per-occurrence cancel needs almost nothing new.** An occurrence *is* a
`Booking` with `RecurrenceRuleId` set, so `GET /bookings/{id}` and
`POST /bookings/{id}/cancel` already view and cancel one occurrence
independently, unchanged, the moment `RecurrenceRuleId` is populated. The only
gap (WP-4 loose end 3 in `docs/wp4-plan.md`) is that the list row doesn't carry
`RecurrenceRuleId` — worth adding now that it can be non-null, additively.

**Whole-series cancel is new**: `POST /recurrence-rules/{id}/cancel`, reusing
`0002`'s reach (owner, or a TenantAdmin in the same tenant) via the same
owner-filter pattern `BookingReadRules` already established for bookings,
generalized to `RecurrenceRule.UserId`.

Sequence, all through EF and **one `SaveChangesAsync`, no `IUnitOfWork`** —
cancelling never adds demand against `Resources.Capacity`, the same argument
that already keeps the single-booking cancel and `BlackoutCascade` out of
`dbo.CreateBooking`'s territory (CLAUDE.md §4.1):

1. Load the rule via the reach filter → `RecurrenceRuleNotFoundException` (404)
   if not visible — same byte-identical-for-three-cases shape as
   `BookingNotFoundException`.
2. Refuse if already `Cancelled` — **`RecurrenceRuleNotCancellableException`**
   (422), for the same reason a second booking cancel is refused rather than
   idempotent (`0002` amendment 3): `RecurrenceRule.Cancel` records an actor and
   a time, so a repeat would overwrite who ended the series.
3. `RecurrenceRule.Cancel(actorUserId, nowUtc)` — the entity already has this
   method; it just needs the not-already-cancelled guard added (it is
   unconditional today, per §2's table).
4. Find every `Booking` with this `RecurrenceRuleId`, `Status` in
   `Pending`/`Confirmed`, `EndsAtUtc > nowUtc` — `0002`'s window, applied per
   occurrence, exactly as if each were cancelled individually — and call
   `Booking.Cancel(actorUserId, "Series cancelled", nowUtc)` on each. A
   self-cancel enqueues no notification, same rule as the single cancel.
5. **One summary notification**, per the owner's answer to shape question 3 —
   not one per occurrence. Addressed to the series owner (or nobody, if the
   owner is the actor, mirroring `0002`'s self-cancel suppression), naming the
   series and how many occurrences it freed.

**A schema consequence this choice forces, found while designing it rather
than guessed at:** `CK_Notifications_HasContext` currently requires
`RecurrenceRuleId` to be paired with `OccurrenceDate` — built for `0008`'s one
specific occurrence, not a whole series. A "series cancelled" summary has no
single occurrence date to anchor to. This needs:

- A new `NotificationKind.SeriesCancelled`.
- `OccurrenceDate` becomes **optional whenever `RecurrenceRuleId` is set**, so
  `CK_Notifications_HasContext` becomes "exactly one of `BookingId` or
  `RecurrenceRuleId` is set" rather than "`BookingId`, or `RecurrenceRuleId` +
  `OccurrenceDate`" — a genuine migration, not just a new enum value.
- A new `Notification` factory, `ForSeriesCancelled(id, recurrenceRuleId,
  recipientUserId, sendAtUtc, createdByUserId, nowUtc)`, alongside
  `ForBooking` and `ForSkippedOccurrence`.

### 5.3 Approvals (FR-7.1–FR-7.5, AC-5)

**Why a capacity re-check is needed even though `Pending` already holds its
claim.** WP-4 established that a `Pending` booking counts toward
`dbo.CreateBooking`'s peak from the moment it is created — nothing "steals"
capacity from an existing `Pending` row through the ordinary create path,
because every later create is checked against the peak that already includes
it. The genuine race is **cancellation freeing capacity that is then
legitimately reused**: member A books an approval-gated, capacity-1 room and
gets `Pending`; A (or an admin) cancels it, freeing the unit; member B then
books the same slot and it is `Confirmed`; an approver, unaware A's request is
already dead, tries to approve it. Approving without a re-check would flip A's
booking to `Confirmed` over B's already-`Confirmed` one — the literal
double-booking FR-4.2 exists to prevent, arriving through the one door that
does not otherwise pass through the lock. `Booking.Status != Pending` alone
(closing WP-4's loose end 1) catches the case where A's booking was actually
cancelled; the capacity re-check is what catches B's slot being taken by a
*different*, still-live booking that never touched A's row at all.

**`dbo.ApproveBooking`** — the same four-part design as `dbo.CreateBooking`
(`0023`), with one structural difference: the row already exists, so the
overlap read excludes it by id rather than inserting a new one.

```
dbo.ApproveBooking(@BookingId, @NowUtc)
  -- read the Pending booking (ResourceId, Quantity) WITH (UPDLOCK) — also the
  -- Status != Pending guard, atomically with the lock, not as a separate read
  -- IF not found or not Pending: RETURN 'BookingNotPending'
  -- read Resources for Capacity/IsArchived, same fail-closed guard as CreateBooking
  -- re-check BlackoutPeriods WITH (HOLDLOCK), same as CreateBooking
  -- overlap read WITH (UPDLOCK, HOLDLOCK), same predicate as CreateBooking,
  --   AND Id <> @BookingId  -- excludes its own already-counted row
  -- peak + this booking's own Quantity > capacity?
  --   RETURN 'SlotUnavailable' / 'CapacityExceeded', same split as CreateBooking
  -- UPDATE Bookings SET Status = 'Confirmed', UpdatedAtUtc = @NowUtc,
  --   UpdatedByUserId = @ApproverUserId WHERE Id = @BookingId
  -- RETURN 'Approved'
```

Reuses `IX_Bookings_Resource_Start` and the exact peak arithmetic — no new
index, no new arithmetic to defend. `IBookingRepository` gains
`ApproveAsync(bookingId, approverUserId, nowUtc)` returning a
`BookingApprovalOutcome` shaped like `BookingCreationOutcome`.

**The handler** (`ApproveBookingCommandRequestHandler`):

1. Load the booking through the **approval reach** (below) →
   `BookingNotFoundException` if not reachable — same 404-not-403 shape as
   cancel, for the same reason (confirming existence leaks who is using what).
2. `_unitOfWork.ExecuteAsync`: call `dbo.ApproveBooking`; on `Approved`, load
   and `ApprovalRequest.Decide(Approved, approverUserId, nowUtc, note)` (EF,
   same transaction), enqueue a `Confirmed` notification to the booker
   (`NotificationKind.Confirmed` already exists and already means "your
   booking is confirmed" — reused, not duplicated), `SaveChangesAsync`.
3. On `BookingNotPending` / `SlotUnavailable` / `CapacityExceeded` /
   `BlackoutPeriod` / `ResourceArchived`, throw the **matching existing
   exception** — all but `BookingNotPending` are already declared and thrown
   by WP-4's create path; AC-5 is satisfied by *reusing* those reason codes,
   not inventing parallel ones. `BookingNotPending` (422, `RuleViolation`) is
   the one genuinely new code.

**Reject needs no procedure**, per the owner's answer to shape question 7:
rejecting removes a claim rather than adding one, so by CLAUDE.md §4.1's own
logic it needs no lock — the same reasoning that already keeps the single
booking cancel and `BlackoutCascade` on plain EF. New domain method
`Booking.Reject(actorUserId, nowUtc)` (guarded by `Status == Pending`,
transitions to `Rejected`), called after `ApprovalRequest.Decide(Rejected, ...)`,
one `SaveChangesAsync`, `NotificationKind.Rejected` reused for the booker.

**The approval reach — occurrence-level, per shape question 6.** A
TenantAdmin may act on any `Pending` booking in their tenant (the same sweeping
reach `0002` already gives them over cancellation); an `Approver` may act only
on a `Pending` booking whose resource lists them in `ApproverUserIds` (`0018`).
Each `Pending` occurrence of a series has its own `ApprovalRequest`, so
"reject one occurrence, leave the rest pending" is simply calling this
endpoint on that occurrence's booking id — no series-aware branching needed in
the approve/reject handlers at all.

**The approver queue — extending `GET /bookings`, per shape question 4.**
`BookingScope.Tenant` currently validates as TenantAdmin-only
(`ListBookingsQueryRequestValidator`). This widens to admit `Approver` too, but
an `Approver` (not also a `TenantAdmin`) gets an **additional** restriction the
repository applies: `ResourceId IN (resources this caller approves for)`. That
resource set does not exist as a queryable projection today —
`Resource.ApproverUserIds` is a computed property over a private-field owned
collection (`ResourceApprovers`), which is why `GET /resources/{id}` already
needed a second query rather than a projection (WP-3 Phase 3's finding). The
new repository method resolving "which resource ids is this user an approver
for" is a small, targeted query against that table; whether it goes through a
raw parameterized `SELECT ResourceId FROM ResourceApprovers WHERE UserId = @id`
(same class of documented deviation `BookingRepository.CreateAsync` already
takes, for the same reason — the shape EF's owned-collection mapping does not
project cleanly) or a `Set<Resource.ApproverAssignment>()` query is a Phase 3
detail to resolve against what EF actually allows, not a design question.

**DTOs gain the booker's name** (loose end 3): `ListBookingsQueryResponse` and
`GetBookingQueryResponse` add `userName` (or `userFirstName`/`userLastName` —
a Phase 3 detail) beside `userId`, following `ApproverDetail`'s
id-and-name-no-email shape rather than adding email. `GetBookingQueryResponse`
also gains an approval section (decision id, requested/expires timestamps,
decision if any) when `RecurrenceRuleId` is irrelevant to it — a `Pending`
booking's detail is where an approver actually decides, so the wire shape
belongs there (loose end 4).

---

## 6. API surface

| Route | Policy | Notes |
|---|---|---|
| `POST /recurrence-rules` | `TenantMember` | 201 with created/skipped/refused occurrences if ≥1 created; 422 `NoOccurrencesCreated` with the same breakdown if none were |
| `POST /recurrence-rules/{id}/cancel` | `TenantMember` | owner or TenantAdmin (0002); cancels remaining occurrences + the rule itself |
| `POST /bookings/{id}/approve` | `TenantMember` | TenantAdmin (any pending in tenant) or an assigned Approver (0018) |
| `POST /bookings/{id}/reject` | `TenantMember` | same reach as approve |
| `GET /bookings?scope=tenant` | `TenantMember` | now also valid for `Approver`, resource-restricted; unchanged for TenantAdmin |

`GET /bookings/{id}`, `GET /bookings`, `POST /bookings/{id}/cancel` are
unchanged in route and policy — WP-5 only widens their reach and DTOs, per
§5.3.

New `TenantMember`-not-`TenantAdmin`-only policy note: unlike WP-3/WP-4's
admin-only writes, `POST /recurrence-rules` is a **member** action (a member
books their own recurring slot, same as a one-off), which is why it sits on
`TenantMember` rather than a new policy — the approval reach is the part that
needs a role check, done inside the handler exactly as `0002`'s cancellation
reach already is, not on the controller attribute.

---

## 7. New reason codes

| Code | Kind | Status | Meaning |
|---|---|---|---|
| `NoOccurrencesCreated` | RuleViolation | 422 | Every occurrence in a series request was skipped or refused |
| `RecurrenceRuleNotFound` | NotFound | 404 | No such series visible to this caller — id, cross-tenant, or another member's, all identical (AC-4) |
| `RecurrenceRuleNotCancellable` | RuleViolation | 422 | The series is already cancelled |
| `BookingNotPending` | RuleViolation | 422 | Approve/reject called on a booking that is not (or no longer) `Pending` |

Everything else approval can refuse — `SlotUnavailable`, `CapacityExceeded`,
`BlackoutPeriod`, `ResourceArchived`, `ResourceNotFound` — reuses WP-3/WP-4's
existing codes and exception subclasses unchanged, per §5.3.

---

## 8. Schema changes

Two migrations, both hand-written `migrationBuilder.Sql(...)` per CLAUDE.md §5:

1. **`AddApproveBookingProcedure`** — `dbo.ApproveBooking`, sketched in §5.3.
2. **`WidenNotificationsRecurrenceAnchor`** — `NotificationKind.SeriesCancelled`
   added to `CK_Notifications_Kind`; `CK_Notifications_HasContext` loosened so
   `RecurrenceRuleId` no longer requires `OccurrenceDate` alongside it.

No change to `RecurrenceRules`, `Bookings`, or `ApprovalRequests` — every
column §5 needs already exists.

---

## 9. Phasing

Four phases, each a slice reviewable and defensible on its own, matching the
delivery style WP-3 and WP-4 used. Chunk boundaries inside a phase are settled
at the start of that phase.

### Phase 1 — Creating a series (FR-5.1, FR-5.4)

Two chunks: **1a** the pure Domain expansion (no DB, no endpoint — reviewable
on its own, same reasoning WP-4 Phase 1a split out `BookingEligibility`
first); **1b** the write path built on it.

#### 1a — `RecurrenceExpansion`. Done 2026-09-09.

907 unit tests pass (28 new), `dotnet build` clean across the solution
(integration tests project also rebuilt clean; the integration suite itself
was not run — no code in this chunk touches EF, RLS or the procedure, so
nothing here could regress it, but that is not the same as having watched it
pass). Delivered: `RecurrenceExpansion.Expand(rule, zone)` and
`RecurrenceOccurrence`/`RecurrenceOccurrenceOutcome` in
`BookSpace.Domain/Availability/`; `RecurrenceRule.OccurrenceDate(int index)`,
extracted alongside a shared `StepDate` helper so `ComputeImpliedEndDate`'s
span-cap arithmetic and the expansion loop can never compute a different date
for the same index; `IResourceTimeZone.IsInvalidLocalTime` and its
`SystemResourceTimeZone` implementation.

Two things found while building it, neither anticipated by §5.1 above:

- **`RecurrenceRule` had no constructor guard against `LocalEndTime <=
  LocalStartTime`, and nothing in the schema backs one either** — unlike
  `AvailabilityWindow`, which `CK_AvailabilityWindows_Window` enforces at the
  DB. Every existing caller (`SeedData`, the unit tests) already passes a
  same-day pair, so this was a live gap rather than a deliberate omission:
  before this chunk, a rule with `LocalEndTime <= LocalStartTime` would
  construct successfully and only fail later, inside `Booking`'s own
  `EndsAtUtc > StartsAtUtc` guard, as an unhandled `ArgumentException` (a 500)
  when the first occurrence was expanded. Added the same check `RecurrenceRule`
  already makes for `IntervalValue` and the end-condition pair — same file,
  same defense-in-depth reasoning. **Smaller call, not asked of the owner**:
  an overnight recurring booking (`LocalEndTime` on the next calendar day) is
  out of scope — FR-5.1 does not ask for one, nothing upstream defines what
  "the next day" would mean for a `Monthly` rule's occurrence date, and the
  fix is a one-line same-day requirement rather than new machinery. 1b's
  validator restates this as a 400, the same way `CreateBookingCommandRequestValidator`
  restates `CK_Bookings_Interval` — this constructor guard is the
  defense-in-depth backstop, not the client-facing rejection.
- **A clocks-forward gap has to be tested at both ends of an occurrence, not
  just the start.** Decision `0008`'s wording names `LocalStartTime`, but a
  gap is up to a few hours wide (`SystemResourceTimeZone`'s own bound is
  four), so a start just before the gap and an end just inside it is a real
  case — tested explicitly
  (`Expand_SkipsAnOccurrenceWhoseLocalEndFallsInTheGapEvenThoughStartDoesNot`).
  This is why `IsInvalidLocalTime` was added to the interface rather than
  inferred from `ToUtcEarliest`'s behavior: that method never throws for a
  gap, it returns the transition instant instead (per D3/`0021`'s
  range-absorption rule), which is a different question from "did this
  local time ever happen."

Also verified, per the project's own rule that a test able to pass
unconditionally proves nothing: `Expand_ResolvesAnAmbiguousOccurrenceUsingTheEarlierInstantForBothEnds`
and its sibling with an unambiguous end both assert against real
`America/New_York` tzdata (2026-11-01 fall-back), not a fixed-offset fake —
the fake cannot make `0024`'s "earlier for both ends" claim, only a real zone
with an actual ambiguous hour can.

#### 1b — the write path. Done 2026-09-09.

947 unit tests pass (36 new), 418 integration tests pass (12 new, one with an
added assertion). Delivered
as planned: `IRecurrenceRuleRepository` + its Infrastructure implementation
(a plain EF add, registered in DI); the `AppException.Extensions`
generalization and `GlobalExceptionHandler`'s generic copy onto
`ProblemDetails.Extensions`; `NoOccurrencesCreatedException` and
`ReasonCodes.NoOccurrencesCreated`; `RecurrenceOccurrenceReport` (shared
between the success response and the exception, per §5.1's design);
`POST /recurrence-rules` on `TenantMember`, its handler, validator and DTOs;
unit tests for the handler's three-bucket partition and the validator's shape
rules; integration tests for the happy path, approval routing, a series
hitting a blackout mid-run, the all-refused 422 case, and the usual
structured-error/authorization sweep.

Four things found while building it, none anticipated by §5.1's sketch:

- **Staging the approval request and notifications inside vs. outside
  `IUnitOfWork`'s delegate is not the same question here as it is for a
  single booking.** `CreateBookingCommandRequestHandler` stages both
  *before* the delegate opens, because a 1205 retry would otherwise re-run an
  unconditional `Add` and double-insert. A series has something that handler
  doesn't: a *next* occurrence to fall through to. Staging unconditionally
  before the delegate meant a declined attempt (no exception, just "not
  Created") left a tracked-but-unsaved `ApprovalRequest`/`Notification` in
  the `DbContext` that the *next* occurrence's `SaveChangesAsync` would then
  try to flush — inserting an `ApprovalRequest` against a `BookingId` that
  was never created, a foreign-key violation waiting to happen the first
  time a real request declined mid-series. Fixed by staging only *inside*
  the delegate, and only after confirming `Created` — retry-safe because the
  pre-built instances (constructed once, before the delegate, with their ids
  already fixed) are the same object reference on every retry, and EF's
  `Add` on an already-tracked instance is a no-op rather than a duplicate.
  Covered by `ReportsAnOccurrenceTheProcedureDeclinesAndStillCreatesTheRest`
  and `ADeclinedOccurrenceOnAnApprovalGatedResourceStagesNoApprovalRequest`.
- **Persisting the `RecurrenceRule` row up front, unconditionally, had a real
  consequence §5.1 didn't settle, raised by the owner after reviewing this
  chunk and fixed the same day (2026-09-09): an all-refused series left an
  orphaned, `Active` `RecurrenceRule` row with zero occurrences.** The
  alternative first considered — deferring the `Add` until the first
  occurrence actually succeeds — is unsafe for the same reason as the point
  above (an `Add` staged for conditional flushing has to survive a 1205
  retry of *that* occurrence without double-adding, which only works cleanly
  for entities scoped to one occurrence's own delegate, not one shared
  across the whole loop). The fix taken instead is a compensating delete: if
  the loop ends with nothing created, `IRecurrenceRuleRepository.Remove`
  removes the rule before `NoOccurrencesCreatedException` is thrown — safe
  unconditionally, because reaching that branch is exactly the condition
  under which nothing else in the database references the row yet. The
  companion half of the same bug — a spring-forward skip's decision-0008
  notification surviving an all-refused series, which would have emailed
  someone 14 days later about an occurrence from a series they were told
  reserved nothing — is fixed by the same restructuring: skipped-occurrence
  notifications are built as plain objects during the loop and only ever
  staged (`AddNotifications`) once the series' overall outcome is known to
  include at least one created occurrence, so an all-refused series never
  adds them at all rather than having to undo an insert. Both halves proved
  able to fail (the compensating delete was commented out; exactly the two
  unit tests below and one integration test failed, nothing else did, then
  the fix was restored). Pinned by `TheRuleIsRemovedAgainWhenEveryOccurrenceIsRefused`,
  `ASkippedOccurrencesNotificationIsNeverPersistedWhenEveryOccurrenceIsRefused`,
  and the integration test's added `RecurrenceRules` count assertion.
- **The `AppException.Extensions` generalization is additive by
  construction, not just by intent** — a new protected constructor overload,
  with the existing three-argument one delegating to it with `extensions:
  null`. No existing exception subclass needed to change, and
  `AppExceptionCatalogueTests.Construct`'s reflection-based instantiation
  (which passes `null` for every reference-type constructor argument) meant
  `NoOccurrencesCreatedException`'s constructor had to tolerate a null
  `occurrences` list rather than assume the handler always supplies one.
- **`RecurrenceRule` had no guard against `LocalEndTime <= LocalStartTime`
  before this chunk** (recorded in Phase 1a's entry above, since the
  constructor change landed there) — restated here because 1b is where a
  client-facing 400 for it was actually added, in
  `CreateRecurrenceSeriesCommandRequestValidator`, mirroring how
  `CreateBookingCommandRequestValidator` restates `CK_Bookings_Interval`.

One thing intentionally **not** done in this chunk, unlike WP-4's pattern of
weakening a guarantee to prove its test can fail: there is no single
"guarantee" here to weaken the way `dbo.CreateBooking`'s lock is. The nearest
analogue — the retry-safety of the staging design above — is exercised
directly by the two tests named for it, but proving *that* a synthetic 1205
mid-series would misbehave without the fix would need real contention, which
is out of scope until Phase 3 gives `dbo.ApproveBooking` (and, by extension,
this design) its own concurrency proof.

### Phase 2 — Occurrence view/cancel and whole-series cancel (FR-5.2, FR-5.3)

**Done 2026-09-09.** 978 unit tests pass (23 new), 430 integration tests pass
(12 new, plus 2 existing files gaining an assertion each). Delivered as
planned: `RecurrenceRuleId` added to `ListBookingsQueryResponse` (per-occurrence
view/cancel needed nothing else — an occurrence *is* a `Booking` with
`RecurrenceRuleId` set, so `GET /bookings/{id}` and `POST /bookings/{id}/cancel`
already worked the moment Phase 1 started populating it);
`POST /recurrence-rules/{id}/cancel`, its handler and validator;
`RecurrenceRule.CanBeCancelled()` and the not-already-cancelled guard on
`Cancel`; `IRecurrenceRuleRepository.FindForCancellationAsync` and
`IBookingRepository.FindOccurrencesToCancelAsync`; `Notification.ForSeriesCancelled`;
the `WidenNotificationsRecurrenceAnchor` migration; unit tests for the handler
(who may cancel, the notification asymmetry, the refusals) and the validator;
integration tests for the happy path, an already-individually-cancelled
occurrence being left untouched, the freed slot being bookable again, the
notification asymmetry, and the AC-4 sweep.

**Found and fixed while building it, before any of the above was written**:
`RecurrenceRules` had no tenant isolation at all — no `OrgId`, no EF query
filter, no RLS predicate. Phase 1 never exposed this (it only ever creates a
rule, scoped implicitly through the resource it belongs to); this phase's
cancel-by-id endpoint is the first thing that loads an *existing*
`RecurrenceRule` by a caller-supplied id, and with decision `0002`'s reach — a
`TenantAdmin`'s owner filter dropped entirely — the query would have had zero
tenant restriction under it. Raised with the owner before writing any of
Phase 2's feature code (the owner chose to fix it in the same pass rather than
as a separate step or a documented stopgap), fixed as
[`0025`](decisions/0025-recurrence-rule-tenant-scoping.md) by applying decision
`0014`'s exact pattern: `OrgId` denormalized from the owning `Resource`, a
composite same-org FK, the query filter, and the RLS predicate. Migration
`AddRecurrenceRuleTenantScoping`, verified by a real revert and re-apply
against the dev database, same as `0014`'s was.
`CancelRecurrenceSeriesEndpointTests.Cancel_RefusesAnAdminReachingIntoAnotherTenant`
is the test that would have caught the gap, and now does; `SeedDataTests` and
`TenantIsolationTests` needed the same `IgnoreQueryFilters()` treatment their
`AvailabilityWindows`/`BlackoutPeriods` assertions already had, since both had
been reading `RecurrenceRules` with no filter to ignore.

**A second, smaller schema change fell out of the design rather than being
anticipated**: the owner's answer to shape question 3 (one summary
notification for the whole series, not one per occurrence) meant
`SeriesCancelled` needed to anchor to a `RecurrenceRuleId` alone, with no
single occurrence date — decision `0008`'s original
`CK_Notifications_HasContext` required `RecurrenceRuleId` *and*
`OccurrenceDate` together, built for its one specific kind
(`RecurrenceOccurrenceSkipped`). Widened as
[`0026`](decisions/0026-notifications-series-anchor.md), a strict widening
verified the same way.

**One design choice worth recording**: the notification is enqueued directly
via `IBookingRepository.AddNotifications`, the same port Phase 1 already uses
for skipped-occurrence notifications, rather than adding an equivalent method
to `IRecurrenceRuleRepository`. Both repositories share the same scoped
`DbContext`, so this is purely a question of which port a call site reaches
through — `IBookingRepository` already generalized past "rows derived from a
booking" once Phase 1 used it for a rule-anchored notification, so a second
non-booking notification through the same port extends a precedent rather
than setting a new one.

### Phase 3 — Approvals (FR-7.1–FR-7.5, AC-5)

`Booking.Reject`; `dbo.ApproveBooking` and its migration; `IBookingRepository
.ApproveAsync`; `POST /bookings/{id}/approve` and `.../reject`, handlers,
validators; the approval reach (TenantAdmin-wide, Approver-own-resources);
`GET /bookings?scope=tenant` opened to `Approver` with the resource
restriction; the booker-name and approval-detail DTO additions. This is
WP-5's hard-problem phase, on the same footing `dbo.CreateBooking` was in
WP-4 — the procedure-level concurrent test (approve racing a competing create
for the same freed slot) is written and shown passing before the HTTP-level
one, mirroring WP-4 Phase 1b/3's split.

### Phase 4 — AC sweep and documentation

Confirm all four acceptance criteria against the full suite; a decision record
for `0023`'s extension is not needed (`0023` already says `dbo.ApproveBooking`
inherits its design — this phase adds the measured evidence to it, the same
way WP-4 Phase 3 extended `0023` rather than writing a new record); tick
WP-5 in CLAUDE.md §12; update the loose-ends table in §4 above to show what
closed and what, if anything, did not.

---

## 10. Suggested next step

This plan is what CLAUDE.md's own process asks be approved before code — the
same checkpoint WP-3's and WP-4's plans passed through. Once confirmed, Phase 1
starts, delivered in small reviewable chunks with control returned between
them.
