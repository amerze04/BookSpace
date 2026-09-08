# WP-5 — Recurrence, Approvals & Time Correctness

Source doc: `docs/Work Packages - Week 4.pdf` (week 4, backend track), which
carries WP-4 and WP-5 together. **That PDF is the authority on scope** — the task
list in CLAUDE.md §12 is copied from it, and anything not in it is a gap to flag
rather than something to add on judgment (CLAUDE.md §11).

---

## Status

**Not started. This document is the handoff brief only — it is not yet a plan.**
Written 2026-09-08 at the close of WP-4, for a fresh session to start from.

The plan itself gets written the way WP-3's and WP-4's were: read the source doc,
settle the shape questions in §6 below with the owner, then write the phasing
into this file and get it approved **before any code**. Do not skip that step;
both previous packages found real design problems during it (WP-4 found the
peak-vs-sum bug in CLAUDE.md §4.1 before writing a line).

**Test baseline to compare against: 879 unit + 406 integration, 0 failed.**
`dotnet build` is clean. WP-4 is complete and its changes are in the working
tree, uncommitted, for the owner to review.

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

Most of the hard groundwork is done. Read this before assuming something needs
building.

| Piece | Where | Note |
|---|---|---|
| `RecurrenceRule` entity | `Domain/Entities/RecurrenceRule.cs` | frequency, interval, local start/end time, start/end date, occurrence count, IANA `TimeZoneId`, `Status`, `Cancel(...)`, and `ComputeImpliedEndDate` |
| `RecurrenceStatus` | `Domain/Enums/` | `Active`, `Cancelled` — only two |
| `Bookings.RecurrenceRuleId` | schema + `NewBooking` | nullable, **always null today**; the create path already accepts it |
| `dbo.CreateBooking` | `AddCreateBookingProcedure` | takes `@RecurrenceRuleId`; every occurrence goes through it |
| `ApprovalRequest` entity | `Domain/Entities/` | `Decide(...)`, `Expire(...)`; rows are **written** by WP-4's create path |
| Approvers on a resource | WP-3 Phase 3 | eligibility settled by `0018`; `Resource.ApproverUserIds` |
| `IResourceTimeZone` | `Domain/Availability/` | `ToUtcEarliest` / `ToUtcLatest` / `ToLocal`, with the DST rules `0021` fixed |
| `AvailabilityCalculator` + `BookingEligibility` | `Domain/Availability/` | the same question "may this interval be booked" that each occurrence asks |
| `NotificationKind.RecurrenceOccurrenceSkipped` | `Domain/Enums/` | already declared for `0008`, with the second anchor on `Notifications` |
| `Notifications` second anchor | schema | `RecurrenceRuleId` + `OccurrenceDate`, for the one kind with no `BookingId` |
| `IUnitOfWork` | `Infrastructure/Persistence/` | the transactional boundary; **its delegate must be safe to run twice** |
| Seeded recurrence rule | `SeedData` | one weekly standup per tenant, running to the two-year boundary, **with no occurrences materialized** |

**What does not exist and WP-5 must build:** `dbo.ApproveBooking`, any
approve/reject transition on `Booking` (it has `Cancel`, `CancelForBlackout`,
`CheckIn`, `MarkNoShow` — and **nothing for approval**), the recurrence expansion
itself, the approver queue read, and every endpoint in §1.

---

## 3. The decisions WP-5 inherits, already settled

Read these before designing anything; four of WP-5's five hard questions are
already answered and the reasoning is not to be revisited.

- [`0007`](decisions/0007-recurrence-materialization-horizon.md) — a series is
  **fully materialized at creation**, **best-effort per occurrence** (not
  atomic), capped at **two calendar years** past its own `StartDate`. There is
  deliberately **no background top-up job**; CLAUDE.md §7 says so explicitly.
- [`0008`](decisions/0008-dst-spring-forward-policy.md) — an occurrence whose
  local time falls in a **spring-forward gap is skipped, not shifted**. The user
  is told immediately in the series-creation response **and** again by email 14
  days before the date, through the existing Reminder dispatch job — no new job.
  This is why `Notifications` has a second anchor.
- [`0001`](decisions/0001-blackout-vs-recurring-series.md) — a blackout has
  **absolute priority** over a series: it cancels every occurrence it overlaps,
  at any status, and the owner is notified. **Already implemented** by WP-3's
  `BlackoutCascade`, which will start cancelling real series occurrences the
  moment WP-5 creates them — so it is worth re-reading that code rather than
  assuming it needs changing.
- [`0003`](decisions/0003-availability-timezone.md) — availability is expressed
  in the **resource's** timezone, not the booker's.
- [`0002`](decisions/0002-tenant-admin-cancellation.md), as amended by WP-4 —
  the cancellation window is `EndsAtUtc`, a TenantAdmin's reach is the same
  owner filter the reads use, a second cancellation is refused, and
  self-cancellation enqueues no notification. FR-5.3's "cancel the whole
  remaining series" has to decide how these four apply per occurrence.
- [`0023`](decisions/0023-booking-concurrency-strategy.md) — **`dbo.ApproveBooking`
  inherits the whole design.** FR-7.5 and AC-5 need the same capacity check at
  approval time, so it takes the same locks, in the same order, over the same
  index. Read this record before writing that procedure; do not invent a second
  strategy.

### The one genuinely open decision — and WP-5 owns it

**The DST fall-back case for a recurring occurrence.** When the clocks go back, a
local wall-clock time occurs **twice** and is ambiguous rather than nonexistent.
`0008` covers spring-forward (the time that does not exist); `0021` covers a
*range* absorbing a repeated hour by being an hour longer. Neither answers this:
an **instant** has to land somewhere, and there are two candidates.

This is now the **only** open item in CLAUDE.md §9. It was reassigned from WP-4
on 2026-09-07, because WP-4 creates bookings from explicit UTC instants supplied
by the client, so no ambiguous local time ever arises in it. Recurrence — where
a rule expands a *wall-clock* time — is where it finally has to be answered, and
that is WP-5's first task.

Note that `IResourceTimeZone` already offers `ToUtcEarliest` and `ToUtcLatest`,
so the mechanism exists; what is missing is the *policy*, and it must be written
up as a numbered decision record (`0024`) rather than chosen silently in code.

---

## 4. Loose ends WP-5 explicitly inherits from WP-4

These were flagged rather than fixed, deliberately. The first one is a live
correctness bug if WP-5 ignores it.

1. **A cancelled `Pending` booking keeps its `ApprovalRequests` row at
   `Pending`.** Nothing withdraws it. **The approve path must check the
   booking's status**, or an approver can approve a cancelled booking. AC-5
   already requires re-checking availability at approval time, so this check
   belongs beside that one. Recorded in `0002`'s amendment and in
   `docs/wp4-plan.md`.
2. **Nothing writes `BookingStatus.Completed`.** That gap is load-bearing in
   three places now — `Booking.CanBeCancelled`, `CanBeCancelledForBlackout`, and
   the blackout cascade's reach — all of which test `EndsAtUtc` rather than
   status precisely because an attended meeting is still `Confirmed`. It is in
   **no** work package. Worth raising with the mentor before WP-5 adds a fourth
   dependency on it.
3. **Read DTOs carry `UserId` but no owner name**, so an admin using
   `scope=tenant` sees opaque GUIDs. WP-5's approver queue is the first screen
   where that is actively unhelpful — an approver needs to know *who* asked.
   `ApproverDetail`'s id-and-name-no-email shape is the precedent.
4. **`GET /bookings/{id}` carries no approval detail** for a `Pending` booking.
   WP-5 owns approvals, so the wire shape belongs with the approver queue.
5. **Reminder rows (FR-8.3) are written by nothing.** Booking creation is the
   natural writer, but nothing dispatches them and cancelling would then have to
   void them. Deferred to the notifications package; if WP-5's source doc asks
   for reminders, that deferral needs revisiting with the owner.

---

## 5. Traps a fresh session must know before touching this

Every one of these has already cost time once.

- **`member2@acme.test` is unusable in an integration test.**
  `AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin` deactivates it
  permanently by design. A test using it **passes alone and fails only in a full
  run**, with a 401 on *login*. Use `approver@acme.test` as a second Acme
  account.
- **The seed now writes six bookings** (WP-4 Phase 3), so any new test asserting
  a tenant-wide booking count starts from three per tenant, not zero.
  `TenantIsolationTests` and `SeedDataTests` assert those exact numbers.
- **`IUnitOfWork`'s delegate must be safe to run twice.** The execution strategy
  replays it on a 1205, so ids are minted and entities staged *before* it opens.
  A series of 100 occurrences inside one unit of work would replay all 100 —
  which is one reason `0007` chose best-effort-per-occurrence rather than atomic.
- **Deadlocks (1205) are routine at contention**, measured in `0023`: sixteen
  across four runs of six tests. They are absorbed by the retry, but anything
  calling `dbo.CreateBooking` **must** be inside `IUnitOfWork` — a raw
  connection with no execution strategy is a configuration production never
  runs in.
- **Recurrence expands in the application layer, in local wall-clock time**,
  then converts to UTC (CLAUDE.md §4.3). Do **not** use SQL Server's
  `AT TIME ZONE`: it takes Windows zone ids and will not match what .NET
  produces from the IANA id in the column.
- **A stored `TimeZoneId` is an IANA id and only an IANA id.** On Windows
  `TimeZoneInfo` resolves Windows ids too, so "can we find it" is not a
  sufficient check — `ITimeZoneCatalog.IsKnownIanaId` also requires
  `TryConvertIanaIdToWindowsId` to succeed.
- **Availability windows cannot cross midnight** (`CK_AvailabilityWindows_Window`
  requires `ClosesAt > OpensAt`), and `23:59:59` means the following midnight
  (`0022`). An overnight recurring booking meets both facts at once.
- **Booking writes go through the procedure. Reads and cancels do not.** §4.1
  governs writes that *add* demand against capacity. Cancelling a series
  reduces demand, so it is EF plus `SaveChanges`, like WP-4's single cancel —
  no procedure, no lock.
- **Verify the tests can fail.** Every WP-4 chunk did: the lock hints removed,
  the owner filter disabled, the notification suppression removed, both
  isolation mechanisms bypassed. For WP-5 the equivalents are the approval
  re-check (AC-5) and the DST policy (AC-3). Never commit the weakened version.
- **Enums serialize as their names app-wide** (`JsonStringEnumConverter`), and
  integration tests must opt in via `Support/TestJson.cs` to read them back.

---

## 6. Shape questions to settle with the owner before writing the plan

Not answers — these are the questions WP-3 and WP-4 each settled up front, in
the same spirit. Several are genuinely the owner's call.

1. **The DST fall-back policy** (the open decision, §3). Earlier instant or
   later? Consistently, or per some rule? Needs decision record `0024`.
2. **What the series-creation response looks like.** `0008` requires that
   skipped occurrences are reported *in the response*, and FR-5.4 requires
   collisions and blackout conflicts surfaced at creation and never dropped
   silently. So one response has to carry created occurrences, skipped ones, and
   refused ones — is a partial success a 201, a 207-ish shape, or something else?
3. **Cancelling "the whole remaining series"** — what "remaining" means
   (`EndsAtUtc` per occurrence, per `0002`'s window?), whether the
   `RecurrenceRule` moves to `Cancelled`, and whether one notification is sent or
   one per occurrence.
4. **Whether editing a series exists at all.** FR-5.1–5.3 name create and
   cancel, not edit. If it is out of scope, say so in the plan rather than
   leaving it ambiguous.
5. **The approver queue's shape** — is it `GET /bookings?scope=tenant&status=Pending`
   (which WP-4's admin scope already almost provides), or its own endpoint? And
   does an Approver see the whole tenant or only resources they approve for?
6. **Reject vs cancel.** `BookingStatus` has both `Rejected` and `Cancelled`.
   A rejected booking presumably becomes `Rejected` — confirm, and confirm
   whether an approver can reject an occurrence of a series independently.
7. **Whether `dbo.ApproveBooking` is one procedure or two operations.** Approve
   needs the locked capacity re-check; reject does not add demand and so, by
   §4.1's own logic, does not need the procedure at all.

---

## 7. Suggested first move

Read the source PDF, then bring §6 to the owner in one pass — that is what WP-4
did (four shape answers in one sitting, before any code), and it is why its plan
survived contact. Write the phasing into this document, get it approved, and only
then start. Deliver in small reviewable chunks with control returned between
them, as WP-3 and WP-4 both did at the owner's request.
