# WP-4 — Core Booking Engine: proposed approach

Status: drafted 2026-09-07, **before any WP-4 code was written**. Four shape
questions were put to the repo owner before this document was written; their
answers are recorded in "Settled before planning" below and the plan is built on
them. Nothing here is implemented yet.

Source: `docs/Work Packages - Week 4.pdf`, which carries **two** work packages —
WP-4 (weeks 3–4, core booking engine) and WP-5 (week 4, recurrence, approvals
and time correctness). This plan covers WP-4 only, both of its task lists.

---

## What WP-4 asks for

> "The heart of the product: creating bookings that respect the rules and,
> critically, never collide. Build it correctly for one user first (Week 3),
> then make it bulletproof under concurrency (Week 4)."

**Week 3 tasks — correct for a single user**

1. Create a one-off booking for an available slot. (FR-4.1)
2. Reject bookings outside availability, inside blackout, or over capacity. (FR-4.3)
3. Return a clear, machine-readable reason on rejection. (FR-4.5)
4. Let a member view and cancel their own bookings. (FR-4.4)
5. Tests for the single-user happy path and each rejection reason.

**Week 4 tasks — correct under concurrency**

6. A test firing two bookings for the same slot simultaneously.
7. A concurrency strategy that makes a double-booking impossible. (FR-4.2)
8. Prove the fix with a concurrent test that passes.
9. Document which strategy was chosen and why.

**Acceptance criteria**

- Given one remaining slot and two simultaneous requests, exactly one succeeds
  and the other gets a clear rejection — never both. (AC-1)
- All rule violations (availability, blackout, capacity) are rejected with clear
  reasons.
- A member can cancel their own booking; the slot is freed.
- The concurrency strategy is documented and defended.

**The work package's open decision** — "can a TenantAdmin cancel another user's
booking, and if so, how is that user notified?" — is **already answered**, by
[`0002`](decisions/0002-tenant-admin-cancellation.md), taken 2026-08-19: yes,
within their own tenant, with `CancelledByUserId` recording the actor distinctly
from the owner, and the affected user notified by email. WP-4 implements it and
raises the record with the mentor rather than re-deciding it.

---

## Where WP-4 starts from

WP-4 is unusual in this project: **most of its inputs already exist**, and the
one thing it needs most does not exist at all.

Already built, and consumed directly:

- **`BookSpace.Domain/Availability/`** — `UtcInterval`, `IntervalAlgebra`,
  `CapacitySweep`, `AvailabilityWindowExpansion`, `AvailabilityCalculator`.
  WP-3 Phase 5 put this in `Domain` **specifically so WP-4 could reuse it**;
  `AvailabilityCalculator`'s header names WP-4's three rejections and says the
  failure mode of writing them twice is "the API offers a member a slot and then
  refuses the booking for it". This plan is where that promise gets tested.
- **`Resource.AllowsBookingDuration` / `CanFitABooking`** — added in the
  2026-09-04 corrections pass precisely so WP-4 would not re-derive the duration
  limits. `AllowsBookingDuration` still has **no production caller**; WP-4 is it.
- **The `Bookings` table, its EF configuration, its query filter, its RLS
  predicate and `IX_Bookings_Resource_Start`** — all in place since WP-1/WP-2.
- **The error contract** — `AppException` subclasses, `ErrorKind`, `ReasonCodes`,
  one mapping arm ([`0016`](decisions/0016-error-contract-and-reason-codes.md)).
  Six booking codes are already declared and waiting for a thrower.
- **`Booking.Cancel(actorUserId, reason, nowUtc)`** — written in WP-1 with
  decision 0002 in mind, never called.
- **The notification precedent** — `BlackoutCascade` already enqueues
  `Notifications` rows for a dispatch job that does not exist yet.

Does not exist, and is the centre of this package:

- **`dbo.CreateBooking`.** It is named in CLAUDE.md §4.1 as a hard rule, cited in
  eleven files, assumed by decision 0017's test carve-out — and it is not in
  `docs/bookspace-schema-v2.sql` and not in any migration. WP-4 writes it.

Also missing, and needed: any transactional boundary at all. WP-2 recorded that
"no explicit `BeginTransaction` calls exist anywhere yet". WP-4 introduces the
first one, and §5 means it has to go through
`Database.CreateExecutionStrategy().ExecuteAsync(...)`.

---

## Settled before planning (owner, 2026-09-07)

1. **No fail-first theatre.** The work package asks for a concurrency test that
   is watched to fail before being fixed. There is no naive path to fail with —
   §4.1 forbids inserting `Bookings` through LINQ or `SaveChanges`, so a
   "check then save" implementation cannot legitimately exist even temporarily.
   `dbo.CreateBooking` is therefore **correct from its first migration**, the
   concurrent test is written once and shown passing, and the defence lives in
   the decision record rather than in a captured failure. The record states the
   race it prevents explicitly, so the reasoning survives without the
   demonstration.
2. **An approval-gated resource produces a `Pending` booking**, plus its
   `ApprovalRequest` row — FR-7.1, and the capacity is genuinely held, since
   `Pending` already consumes units everywhere else in this system. WP-4 builds
   **no approve/reject endpoints**; those are WP-5's task list.
3. **Notification rows are enqueued, nothing is sent.** `Confirmed` on a
   confirmed create, `ApprovalRequested` per approver on a pending one,
   `Cancelled` on cancellation — inserted in the same transaction as the booking
   write, exactly as `BlackoutCascade` does. Dispatch and email stay out of scope.
4. **Both cancellation paths, plus admin read.** A member cancels their own; a
   TenantAdmin cancels any booking in their tenant (decision 0002).
   `GET /bookings` returns the caller's own by default, with a TenantAdmin-only
   widening filter — which WP-5's approver queue then builds on.

---

## The hard problem: the concurrency strategy

### The rule as CLAUDE.md §4.1 currently states it is wrong

§4.1 says the procedure "compares the **sum** of overlapping `Quantity` against
`Resources.Capacity`". That is not the right arithmetic, and on a pooled resource
it produces false rejections.

Capacity 2. Existing bookings A `[09:00–10:00)` qty 1 and B `[10:00–11:00)`
qty 1. A request for `[09:30–10:30)` qty 1 overlaps both, so the *sum* is 2 and
`2 + 1 > 2` rejects it. But A and B never coexist: at every instant of the
request, exactly one unit is held, so one is free throughout and the booking is
legal. The availability query would have offered this slot —
`CapacitySweep` computes the **peak concurrent** figure, not a sum — and the
booking would then be refused. That is precisely the drift
`AvailabilityCalculator`'s header warns about, arriving from the SQL side
instead of the C# side.

The procedure must compute the **peak concurrent committed quantity across the
requested interval**, which is what decision `0005` means by "concurrent units"
and what `IResourceRepository.PeakConcurrentBookedQuantityAsync` already computes
for the capacity-decrease check. §4.1's wording needs correcting; the guarantee
it describes does not change.

Because the peak of a step function occurs at a step, only two kinds of instant
need testing: the request's own start, and the start of each overlapping booking
inside the request. That makes it a small correlated aggregate rather than a
real sweep.

### Chosen: `UPDLOCK, HOLDLOCK` key-range locks on the overlap read

Sketch — the shape, not the final text:

```sql
CREATE PROCEDURE dbo.CreateBooking
    @BookingId UNIQUEIDENTIFIER, @ResourceId UNIQUEIDENTIFIER, @UserId UNIQUEIDENTIFIER,
    @RecurrenceRuleId UNIQUEIDENTIFIER = NULL, @StartsAtUtc DATETIME2(0), @EndsAtUtc DATETIME2(0),
    @Quantity INT, @Title NVARCHAR(200) = NULL, @Status NVARCHAR(20),
    @CreatedByUserId UNIQUEIDENTIFIER, @NowUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ownTransaction BIT = 0;
    IF @@TRANCOUNT = 0 BEGIN SET @ownTransaction = 1; BEGIN TRANSACTION; END

    -- Read through the RLS-filtered table on purpose: see "the fail-open trap".
    DECLARE @orgId UNIQUEIDENTIFIER, @capacity INT;
    SELECT @orgId = OrgId, @capacity = Capacity
    FROM dbo.Resources
    WHERE Id = @ResourceId AND IsArchived = 0;

    IF @orgId IS NULL ... RETURN 'ResourceNotFound';

    ;WITH Overlapping AS (
        SELECT b.StartsAtUtc, b.EndsAtUtc, b.Quantity
        FROM dbo.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
        WHERE b.ResourceId = @ResourceId
          AND b.Status IN ('Pending','Confirmed')
          AND b.StartsAtUtc < @EndsAtUtc          -- half-open overlap, both sides
          AND b.EndsAtUtc   > @StartsAtUtc
    ),
    Boundaries AS (
        SELECT @StartsAtUtc AS T
        UNION SELECT StartsAtUtc FROM Overlapping WHERE StartsAtUtc > @StartsAtUtc
    )
    SELECT @peak = ISNULL(MAX(u.Used), 0)
    FROM Boundaries AS bd
    CROSS APPLY (
        SELECT SUM(o.Quantity) AS Used FROM Overlapping AS o
        WHERE o.StartsAtUtc <= bd.T AND o.EndsAtUtc > bd.T
    ) AS u;

    IF @peak + @Quantity > @capacity ... RETURN 'SlotUnavailable' / 'CapacityExceeded';

    INSERT INTO dbo.Bookings (...) VALUES (...);   -- @NowUtc, not SYSUTCDATETIME()
    ... RETURN 'Created';
END
```

Why this one:

- **The lock is taken by the very query that does the checking.** There is no
  separate step to forget. Any future write path that reads overlaps this way
  gets the guarantee; a path that does not is visibly not using the procedure,
  which §4.1 already forbids.
- **`IX_Bookings_Resource_Start` makes the range narrow.** The seek is
  `ResourceId = @r AND StartsAtUtc < @EndsAtUtc`, and the key-range lock covers
  exactly that, per resource. The index also `INCLUDE`s `EndsAtUtc`, `Status` and
  `Quantity`, so the whole check is covered and no base-table lookups widen the
  lock. This is why §4.1 calls that index load-bearing.
- **Hints, not `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE`.** The hints raise
  one statement to serializable behaviour; the session-level setting would raise
  every read in the transaction, including the resource lookup, for nothing.
- **Retry on 1205 is already configured.** `EnableRetryOnFailure(5, 10s,
  errorNumbersToAdd: [1205])` has been in `DependencyInjection.cs` since WP-2.
  Two transactions taking overlapping ranges in different orders deadlock; one is
  chosen as victim and retried, and the retry sees the winner's committed row.
  That is the design working, not a defect.

**Honest cost, to be written into the record:** the lower bound of the seek is
open — `StartsAtUtc < @EndsAtUtc` has no floor — so the range lock covers the
resource's history as well as its future. In practice concurrent creates on one
resource serialize against each other regardless of whether their times overlap.
Two things make this acceptable: contention is per resource, not per table, and
the alternative (bounding the range at `@StartsAtUtc − MaxDurationMinutes`) is a
**correctness risk for a performance win** — a long booking made before the limit
existed, or before it was lowered, would fall outside the bound and be missed
entirely, silently overbooking. Deliberately not done.

### The fail-open trap, and why the procedure reads `Resources` first

`Security.TenantAccessPolicy` is a **filter** policy, so it filters the
procedure's `SELECT`s but does not block its `INSERT`. If the session context is
ever missing — an unset `TenantInit`, a connection that dodged
`TenantSessionContextInterceptor` — the overlap query returns **zero rows** and
the capacity check passes trivially. That is an overbooking that no lock can
prevent, because the lock was correctly taken over an empty set.

The guard is to read the resource through the same filtered table and refuse when
it is invisible. No session context means no `Resources` row either, so the
procedure fails closed with `ResourceNotFound` instead of quietly overbooking.
It costs one seek and turns the worst failure mode in the design into a 404.

### Rejected alternatives

- **`sp_getapplock` keyed on the resource id.** Genuinely attractive: deadlock
  free, independent of the query plan, and it gives exactly per-resource
  serialization. Rejected because the guarantee moves *off the data and into a
  string*. Nothing forces a future statement to take the lock first, and the
  failure is silent; a range lock taken by the checking query cannot be skipped
  without skipping the check. It also serializes a resource unconditionally,
  including provably non-overlapping times, which the range lock at least
  narrows by resource *and* by upper bound. Kept as the fallback if the range
  lock proves to deadlock more than retry can absorb — a measurement, not a
  guess.
- **A filtered unique index.** Cannot express overlap. It solves "one booking per
  exact start instant per resource", which is neither what capacity means
  (`0005`) nor what FR-4.2 asks for. The work package says this too.
- **Optimistic concurrency on `RowVersion`.** `Bookings.RowVersion` exists and
  works, but it protects *updates to a row that already exists*. Two inserts
  contend over no row at all, so there is nothing to be stale about.
- **Application-level locking (a semaphore, a distributed lock).** Wrong layer,
  and false the moment there are two API instances — which the Azure hosting
  target makes likely.

---

## The write path, end to end

Tiers per CLAUDE.md §6. The interesting question is which checks live where, and
the answer is not "all of them in the procedure".

| Check | Tier | Where |
|---|---|---|
| `EndsAtUtc > StartsAtUtc`, `Quantity > 0`, status domain | 1 | Existing `CHECK` constraints |
| Request shape (required fields, UTC kind, whole seconds) | 0 | FluentValidator |
| Resource exists / not archived | 4 | Handler — `ResourceNotFound`, `ResourceArchived` |
| Duration against `Min/MaxDurationMinutes` | 4 | `Resource.AllowsBookingDuration` |
| Interval already in the past | 4 | Handler |
| Outside availability windows | 4 | Domain — new `BookingEligibility` |
| Inside a blackout | 4 **and 2** | Domain, **and re-checked in the procedure** — see below |
| Peak concurrent quantity vs capacity | 2 | `dbo.CreateBooking`, under the range lock |
| Approval routing | 4 | Handler — `Pending` + `ApprovalRequest` |

**Why availability and blackout are pre-checked outside the lock.** Neither input
is written by bookers, so neither races with a booking. They are written by
admins, rarely, and a concurrent admin write is not the scenario FR-4.2 is about.
Putting them under the lock would widen it for no guarantee.

**Why the blackout is nonetheless re-checked inside the procedure** (a small call
needing a nod — see below). Decision `0001` gives a blackout *absolute* priority:
a booking must never survive inside one. Between the handler's check and the
procedure's insert there is a window in which an admin's blackout cascade can
run, select the bookings to cancel, and miss this one because it does not exist
yet. §6's own rule of thumb — "must never" belongs in tiers 1–3 — puts that check
in the procedure. It also introduces a deliberate lock-order inversion with the
cascade (the cascade writes the blackout then reads bookings; the procedure reads
bookings then reads blackouts), so the two can deadlock — and the deadlock, with
retry, *is* the serialization. Availability windows get no equivalent treatment,
and the asymmetry is the justification: narrowing a window cancels nothing, so
"inside a window" is not an invariant the system maintains after creation, while
"outside every blackout" is.

**Ordering of the rejections.** Fixed and asserted, so the reason a client gets
is stable: resource → archived → duration → past → availability → blackout →
capacity. Cheapest and most structural first; the one requiring a lock last.

### The handler, in sequence

1. Load the resource with its schedule (`IAvailabilityRepository.FindWithScheduleAsync`
   already returns exactly this). 404 covers the cross-tenant id (AC-4).
2. Reject archived, bad duration, wholly-past interval.
3. Load blackouts and booked quantities over the requested span — the same two
   repository calls the availability endpoint makes.
4. `BookingEligibility.Evaluate(...)` — new pure Domain function, built on
   `AvailabilityWindowExpansion` / `IntervalAlgebra` / `CapacitySweep`, returning
   which of `OutsideAvailability` / `BlackoutPeriod` / `CapacityExceeded` applies,
   or none. It must answer *which*, which is why it cannot simply call
   `AvailabilityCalculator.BookableIntervals` — that collapses all three into a
   gap on purpose (`0020`).
5. Decide the status: `Pending` if `resource.RequiresApproval`, else `Confirmed`.
6. Inside one transaction (`IUnitOfWork`, below): call `dbo.CreateBooking`; on
   `Created`, add the `ApprovalRequest` (if pending) and the `Notifications` rows
   and `SaveChangesAsync`. On a rejection code, throw the matching exception.

Steps 3–4 are a real second query pass over the same data the procedure will
lock, and that is accepted: it is what lets the API answer *why* in a way the
procedure's single result code cannot, and it is three cheap indexed reads.

### The new transactional boundary

The booking insert goes through raw SQL and the derived rows go through EF, so
the two must share a transaction or a crash between them leaves a booking whose
notification never existed. §5 forbids calling `BeginTransaction` directly under
`EnableRetryOnFailure`, so WP-4 adds one Application port:

```csharp
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);
}
```

implemented in Infrastructure over
`Database.CreateExecutionStrategy().ExecuteAsync(...)` with a transaction inside
it. The procedure detects `@@TRANCOUNT > 0` and joins the ambient transaction
rather than opening its own. `@BookingId` is chosen by the caller before the
delegate runs, so a retry re-inserts the same id rather than minting a second
booking. WP-5's approval path needs the identical shape, which is why this is a
port and not a private helper.

---

## API surface

| Route | Policy | Notes |
|---|---|---|
| `POST /bookings` | `TenantMember` | 201 with the created booking; `Confirmed` or `Pending` |
| `GET /bookings` | `TenantMember` | paged; caller's own by default; filters `from`/`to` (overlap), `status`, `resourceId`, and TenantAdmin-only `userId` |
| `GET /bookings/{id}` | `TenantMember` | own; a TenantAdmin sees any in-tenant booking |
| `POST /bookings/{id}/cancel` | `TenantMember` | optional `reason`; member = own only, TenantAdmin = any in tenant |

- **`POST .../cancel`, not `DELETE`** — §4.5 deletes nothing, and cancellation
  records an actor, a time and a reason. Same argument that made archive a POST.
- **A booking id the caller may not see is `BookingNotFound`, not a 403.** Same
  reasoning as `0018` and `0019`: a 403 confirms the id exists. Another member's
  booking, another tenant's booking and a nonexistent id are byte-identical.
- **Times on the wire are UTC instants**, not local wall clock. The availability
  endpoint already answers in UTC instants, so a client picks an interval from
  that response and echoes its bounds back. This is also why WP-4 cannot answer
  the DST fall-back question (below): a one-off booking never names an ambiguous
  local time.
- **`quantity` defaults to 1**, matching the availability query's parameter
  (`0020`'s amendment). On an exclusive resource it can only be 1.
- **Sorting**: whitelist `startsAtUtc` (default), `createdAtUtc`, `status`, with
  `Id` as the unique tiebreak — `ToPagedResultAsync` throws on a non-unique
  ordering (`0015`).
- DTOs per `0015`'s amendment: `…CommandRequest` / `…QueryRequest` naming, one
  response record per endpoint in its own file, request records nested in the
  controller.

### Reason codes

Four new, all needing a `sealed` `AppException` subclass (`0016`'s amendment):

| Code | Kind | Status | Meaning |
|---|---|---|---|
| `BookingNotFound` | NotFound | 404 | No such booking visible to this caller |
| `BookingNotCancellable` | RuleViolation | 422 | Already terminal, or already ended |
| `BookingDurationOutOfRange` | RuleViolation | 422 | Violates `Min`/`MaxDurationMinutes` |
| `BookingInThePast` | RuleViolation | 422 | The requested interval has already ended |

Six existing codes get their first thrower: `SlotUnavailable`,
`CapacityExceeded`, `OutsideAvailability`, `BlackoutPeriod` (all four declared by
§6 and waiting since WP-3), plus `ResourceNotFound` and `ResourceArchived`
reused. `ApprovalRequired` is the one that does **not** — see the smaller calls.

---

## Phasing

Three phases. Each is a slice that can be reviewed and defended on its own;
chunk boundaries inside a phase are settled at the start of that phase, per the
delivery style WP-3 used.

### Phase 1 — Creating a booking (tasks 1, 2, 3, and half of 5)

The whole write path, in three chunks:

- **1a — Domain and contract. Done 2026-09-07.** `BookingEligibility` + its
  result type in `BookSpace.Domain/Availability/`; the four new reason codes and
  their exception subclasses; the `IUnitOfWork` port. 722 unit tests pass (56
  new), 279 integration tests re-run unchanged.
  Three things the plan did not anticipate:
  - **`IResourceTimeZone` needed a third method, `ToLocal`.** A booking arrives
    as UTC instants and the weekly schedule is resource-local wall clock, so the
    local dates to expand over have to be derived from the request — and the
    interface only converted local→UTC. One method rather than a pair, and the
    asymmetry is real: an instant always has exactly one offset, so this
    direction can never be ambiguous the way the reverse trip can.
  - **The two capacity refusals need two sweeps, not one number.** The runs
    `CapacitySweep` returns can be wider than the request and each carries the
    floor across its *own* span, so reading the remaining figure off the run
    containing the request would report a number about the wrong interval — a
    busy morning would make a free afternoon look full. Asking the containment
    question twice (once at the requested quantity, once at 1) is the only
    reading that stays true. Skipped entirely at quantity 1, which is also why
    an exclusive resource can only ever report `SlotUnavailable`.
  - **A guard test already mirrored §6's list in code** (`ReasonCodesTests
    .SectionSixCodesAreAllPresent`), so deleting `ApprovalRequired` failed the
    suite until CLAUDE.md §6 and the test moved with it. Working as intended.
- **1b — The procedure. Done 2026-09-07.** 722 unit + 299 integration tests
  pass (20 new, all integration). Delivered as planned, plus the evidence
  section of `0023`. Four things worth recording:
  - **The tests were verified to be able to fail.** Removing
    `WITH (UPDLOCK, HOLDLOCK)` from the procedure and changing nothing else made
    exactly the four concurrency tests fail and left the other sixteen passing:
    20 simultaneous requests for one slot produced **3** bookings, and a pool of
    4 was filled **10** times. This is what replaces the work package's
    watch-it-fail step, and it is stronger — it indicts *this* procedure with one
    thing removed, not a different implementation nobody would ship. The
    weakened version was never committed.
  - **The overlapping rows go into a table variable before the arithmetic.**
    The lock is then taken by one obvious statement and the peak calculation is
    plain SQL over a local, rather than table hints buried in a CTE that is
    expanded twice. `HOLDLOCK` holds the range to the end of the transaction
    either way.
  - **`BookingRepository` uses raw ADO, not `ExecuteSqlAsync`/`FromSql`** — a
    stated deviation from CLAUDE.md §5's usual instruction. The procedure returns
    two columns and `Database.SqlQuery<T>` reads one scalar column, so the
    alternatives were a keyless entity type in the model to describe a
    procedure's result, or output parameters bolted onto an interpolated `EXEC`.
    Every value is a typed `SqlParameter`, so §5's actual rule — never
    concatenation — is met more strictly than interpolation meets it. The
    connection is opened through `Database.OpenConnectionAsync`, never the raw
    `DbConnection`, or `TenantSessionContextInterceptor` would not fire and the
    procedure would fail closed on every call.
  - **`IBookingRepository` carries only `CreateAsync` for now.** The EF-side
    writes (the `ApprovalRequest` and `Notifications` rows) are added in 1c, when
    the handler that builds them exists and their shape is known.
- **1b as planned — The procedure.** `dbo.CreateBooking` and the
  `AddCreateBookingProcedure` migration (with a `Down` that drops it, verified by
  a real revert and re-apply, as every WP-3 migration was); `IBookingRepository`
  and its implementation; **and the parallel-procedure test straight away** —
  N connections calling the procedure directly, asserting exactly one insert.
  Proving the lock before anything is built on it is worth more than saving the
  test for Phase 3, and at this layer it needs no HTTP at all.
  **Decision `0023` lands here too**, not in Phase 3 as first planned (revised
  2026-09-07): the procedure *is* the strategy and this chunk already proves it,
  so a record written two phases later would justify a choice rather than record
  it. Phase 3 adds the measured end-to-end evidence to it.
- **1c — The endpoint. Done 2026-09-07.** 779 unit + 326 integration tests pass
  (57 new). Delivered as planned: `POST /bookings`, the handler, validator and
  DTOs; the four remaining exception subclasses; approval routing (`Pending` +
  `ApprovalRequest`); the `Confirmed` / `ApprovalRequested` notification rows;
  and the reason-code table extended to the create path.
  Three things worth recording:
  - **A deadlock was observed at ten-way contention, with the lock in place**,
    during the first full-suite run. It is not a defect and the fix was not in
    the procedure: the procedure-level test called it over a raw connection with
    no execution strategy, so it lacked the 1205 retry that every production
    call has through `IUnitOfWork`. The test now retries the way production
    does. `0023`'s first draft overstated `UPDLOCK` as making contenders "block
    instead of deadlocking" — corrected to *usually* block, with the observation
    recorded as evidence for why the retry is part of the design rather than a
    safety net.
  - **Everything is staged before the unit of work opens.** The booking's id is
    minted outside the delegate and the `ApprovalRequest` and `Notifications`
    are added outside it too, because a retry re-runs the delegate: an id minted
    inside would insert a second booking, and entities added inside would be
    added twice — a rollback discards the rows but not the `ChangeTracker`
    entries, so the second attempt would try to insert two approval requests
    against `UQ_ApprovalRequests_Booking`. The delegate is therefore only "call
    the procedure, then save".
  - **Capacity is deliberately checked twice**, and dropping the pre-check would
    still be correct — it would just cost the API its ability to explain itself.
    A booker asking for a plainly full slot would get the same bare 409 as one
    who lost a race by a millisecond (FR-4.5).
- **1c as planned — The endpoint.** `POST /bookings`, the handler, validator and
  DTOs; approval routing (`Pending` + `ApprovalRequest`); the `Confirmed` /
  `ApprovalRequested` notification rows; unit tests for each rejection and
  integration tests for the happy path and the reason-code table.

### Phase 2 — Viewing and cancelling (task 4, rest of task 5)

`GET /bookings`, `GET /bookings/{id}`, `POST /bookings/{id}/cancel` for both
member and admin; the `Cancelled` notification row; the AC-4 sweep over the new
routes (another member's id, another tenant's id, both 404); and the
"slot is freed" test — cancel, then assert the availability endpoint offers the
interval again and a new booking for it succeeds.

Two chunks, agreed with the owner on 2026-09-08: **2a** the two reads, **2b**
the cancel plus the AC sweep and the slot-freed test.

#### Settled before Phase 2 (owner, 2026-09-08)

Six questions, four of which the plan had left to whichever phase hit them.

1. **A handler learns the caller's role through `ICurrentUser.IsInRole(Role)`.**
   Nothing in `BookSpace.Application` could read a role before this — RBAC had
   been entirely declarative on the controllers — but decision `0002` puts the
   TenantAdmin check "in the Application layer, not the Domain entity", and it
   cannot be a policy on the action: the same route serves a member and an admin,
   and the difference is in *which rows* are visible rather than in whether the
   route may be called. Takes the Domain enum rather than a string, so a handler
   cannot mistype a role name and silently never match. Rejected: a bare
   `IsTenantAdmin` flag (WP-5 needs `Approver` too), a separate roles port, and
   separate admin-only routes — the last would give one booking two URLs and
   split the 404-not-403 rule across them.
2. **`GET /bookings` gains an admin-only tenant-wide scope**, alongside the
   per-member `userId` filter the plan already had. "What is booked next week" is
   the admin's real question and `userId` can only answer it one member at a
   time, and WP-5's approver queue needs the same widening. Own-bookings stays
   the default **for everyone including a TenantAdmin**, who is a member of their
   tenant before they are its admin.
3. **The booking read DTOs carry `resourceName`** beside `resourceId`, on both
   the list row and the detail. A member's list spans resources by definition, so
   ids alone force a fetch per row to render anything a person could read.
   Rejected: also carrying the resource's `TimeZoneId` (not needed while every
   instant on the wire is UTC), and ids only.
4. **Self-cancellation enqueues no `Cancelled` notification** (smaller call 6,
   closed). Decision `0002`'s requirement is that the *affected user* is told;
   emailing someone the news they just made is noise. A cancellation by anyone
   else does enqueue one. Lands in 2b.
5. **A non-admin sending an admin-only parameter gets `ValidationFailed` 400**
   with a per-field error (smaller call 8, closed, now covering `scope` as well
   as `userId`). `ErrorKind` has no `Forbidden` and inventing one for a
   query-string filter is heavier than the problem, so `0016`'s map is unchanged.
   Rejected: adding `ErrorKind.Forbidden → 403`, and silently ignoring the
   parameter — which would answer a different question than was asked.
6. **`from`/`to` default to the whole history**, not to "upcoming", following
   `GET /resources/{id}/blackout-periods`. Paging and the default chronological
   sort bound the response either way, and defaulting to now would leave "what
   did I book last month" unanswerable through the only endpoint that asks it.

Also settled, and **beyond what the plan recorded**: the amendment to `0002` that
2b writes should cover the *read* widening too, not only the cancellation window
and the notification suppression. It is the same principle — how far a
TenantAdmin's reach extends inside their own tenant — and `0002` currently
describes only the cancel.

#### 2a — The two reads. Done 2026-09-08.

849 unit + 373 integration tests pass (70 unit, 47 integration new). Delivered:
`GET /bookings` (paged, `from`/`to` overlap, `status`, `resourceId`, admin
`userId`/`scope`) and `GET /bookings/{id}`, both on `TenantMember`;
`ICurrentUser.IsInRole`; `BookingReadRules`, `BookingOwnerFilter`,
`BookingScope`, `BookingSortFields`; the two reads on `IBookingRepository`; and
`POST /bookings`'s `Location` header, which Phase 1c deliberately left off
because there was nothing to point at.

Five things worth recording:

- **The visibility rule is pushed into the query, not applied after the read.**
  The handler resolves a `BookingOwnerFilter` and the repository applies it as a
  `WHERE` clause, so a booking the caller may not see never materializes — which
  leaves the handler exactly one branch, null → `BookingNotFound`. A
  load-then-compare would leave a moment where someone else's booking exists in
  memory one early return away from the wire, and would make the 404 a thing a
  handler remembers to do rather than a thing the query cannot avoid.
- **`BookingOwnerFilter` is a type rather than a `Guid?`**, and the reason is
  fail-open. The repository needs "this user" or "anyone in the tenant", and a
  bare nullable spells the second as `null` — so a handler that forgot to set it,
  or a new caller that defaulted the argument, would silently widen a member's
  list to the whole tenant. `AnyOwner` has to be asked for by name and there is
  no public constructor, so the widened state is unreachable by accident. This is
  CLAUDE.md §4.2's objection one scope down: tenant isolation would still hold,
  *member* isolation would not.
- **The privilege is checked twice, deliberately** — once in the validator, which
  is what produces the client-facing 400, and again in `BookingReadRules`, which
  is the gate. Same shape as capacity being checked twice on the create path: a
  future caller that dispatches the query without the pipeline, or a validator
  someone forgets to type against a new query record (CLAUDE.md §12's discovery
  gotcha), still cannot widen a member's view.
- **The validator injects `ICurrentUser`, which is a first in this codebase.**
  It works because `AddApplication` registers each `IValidator<T>` by type
  through DI rather than as an instance, so constructor injection is activated
  normally. Worth knowing before the next validator needs a dependency.
- **The tests were verified to be able to fail.** Disabling the owner filter in
  the repository and changing nothing else made **exactly the five**
  member-isolation tests fail and left the other 42 passing — and notably
  `AdminScope_NeverReachesAnotherTenant` still passed, which is the evidence that
  the tenant filter is independent of the owner filter rather than being
  accidentally load-bearing for it. Same technique Phase 1b used on the lock
  hints; the weakened version was never committed.

Three smaller calls taken inside the chunk, none needing a question:

- **`userId` and `scope=tenant` together are refused**, rather than given a
  precedence rule. Together one of the two has to be ignored, and an
  accepted-then-ignored parameter is the shape settled answer 5 rejects
  everywhere else. `userId` alone already searches the whole tenant for that
  member, so nothing is lost.
- **An unknown `resourceId` is an empty page, not a 404**, deliberately unlike
  `GET /resources/{id}/blackout-periods`. There the resource is in the *route*,
  so "no such room" has to be distinguishable from "no blackouts"; here it is one
  filter among five on a collection that belongs to the member, and "no bookings
  match" is honest for every combination of them. It also avoids confirming that
  a resource id exists.
- **`RecurrenceRuleId` is on the detail but not the list row.** WP-4 never writes
  it, so on the list it would be a column null on every row; WP-5 makes
  occurrences independently viewable (FR-5.2) and can add it then, additively.

One gotcha found the hard way, and it is a **fixture trap rather than a bug**:
`member2@acme.test` is unusable as a second Acme account.
`AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin` deactivates it
permanently and never restores it — its sibling's comment says so outright. Using
it here passed in isolation and failed only in a full run, with a 401 on *login*,
which is precisely the from-a-distance failure that comment warns shared fixtures
produce. These tests use `approver@acme.test` instead, which costs nothing: the
rule under test is "a non-admin sees only their own bookings", and an Approver is
a non-admin — if anything the stronger choice, since the `Approver` policy sits
between Member and TenantAdmin.

#### 2b — The cancel. Done 2026-09-08.

879 unit + 395 integration tests pass (30 unit, 22 integration new). Delivered as
planned: `POST /bookings/{id}/cancel` on `TenantMember` for both actors;
`Booking.CanBeCancelled` and the tightened `Cancel` guard;
`IBookingRepository.FindForCancellationAsync`; the `Cancelled` notification row
with self-cancel suppression; the amendment to
[`0002`](decisions/0002-tenant-admin-cancellation.md); and the slot-freed test.

Both changes the plan named above landed as written — the domain guard, and no
`IUnitOfWork`. The contrast with the create path is now the clearest statement in
the codebase of what §5's rule is actually about: **mixing raw SQL with EF is
what forces an execution strategy**, not "a write" in general. Cancel is one
`SaveChangesAsync`, which is already a transaction, so it needs neither.

Four things worth recording:

- **The slot-freed criterion is asserted three ways, not one.** On an exclusive
  resource the same interval is refused *before* the cancel (409
  `SlotUnavailable`) and accepted after it — by a different member, so the second
  create could only succeed if the first booking genuinely stopped holding its
  unit. Separately, the availability endpoint goes back to offering one
  continuous span; and on a pooled resource the *unit* comes back rather than the
  time, which is decision `0005`'s model rather than mutual exclusion.
- **The notification suppression keys off actor-vs-owner, not off the role**, and
  that distinction is tested: an admin cancelling their *own* booking also gets
  no row. A role-based check would have got that case wrong, and it is the kind
  of thing that would only surface as an odd email months later.
- **Both new behaviours were verified to be able to fail.** Disabling the owner
  filter in `FindForCancellationAsync` made exactly the two member-isolation
  tests fail — and `Cancel_RefusesAnAdminReachingIntoAnotherTenant` still passed,
  the same independence result Phase 2a got. Forcing the notification
  unconditionally made exactly the two suppression tests fail at *both* levels,
  unit and integration. Neither weakened version was committed.
- **Decision `0017`'s carve-out is genuinely still needed, exactly as the plan's
  smaller call 9 predicted.** Two states this endpoint must refuse cannot be
  reached through the API at all: a wholly-past booking (`POST /bookings` refuses
  it with `BookingInThePast`) and one already in progress. Both are arranged with
  the raw-SQL fixture, which is the carve-out narrowing rather than expiring —
  new tests use the real path, and only states the endpoint cannot produce fall
  back to SQL.

One thing raised rather than decided, and it belongs to WP-5: **cancelling a
`Pending` booking leaves its `ApprovalRequests` row at `Decision = 'Pending'`.**
Nothing withdraws it. That is consistent with this plan's own scope statement
("WP-4 creates the `Pending` booking and the `ApprovalRequest` row those
endpoints will decide") and WP-5's approve path has to check the booking's status
regardless, since AC-5 already requires re-checking at approval time. But it is a
real loose end: today an approver in WP-5 could approve a cancelled booking
unless that check is written. Recorded in `0002`'s amendment and flagged here so
WP-5 inherits it explicitly rather than discovering it.

### Phase 3 — Concurrency, proof and documentation (tasks 6–9)

**Done 2026-09-08. This completes WP-4.** 879 unit + 406 integration tests pass.
The handoff brief and the build plan below are kept as written — the brief was
the input to this phase and the plan is what was agreed before any code — and
the outcome is recorded at the end of the section, under "What Phase 3 actually
produced".

#### Where the build stands

Phases 1 and 2 are complete. `dotnet build` is clean and `dotnet test` gives
**879 unit + 395 integration, 0 failed** — that is the baseline to compare
against, and every number below was measured on it. The working tree is clean as
of commit `cf892c9` plus Phase 2b's changes.

What exists and works:

| Piece | Landed | Note |
|---|---|---|
| `dbo.CreateBooking` + `AddCreateBookingProcedure` | 1b | the guarantee; `0023` documents it |
| `POST /bookings` | 1c | `Confirmed` or `Pending`, + `ApprovalRequest` and `Notifications` rows |
| `GET /bookings`, `GET /bookings/{id}` | 2a | own by default; admin `userId` / `scope=tenant` |
| `POST /bookings/{id}/cancel` | 2b | owner or TenantAdmin; `Cancelled` row, suppressed on self-cancel |
| `IUnitOfWork` | 1a | the only transactional boundary; create only, not cancel |
| `ICurrentUser.IsInRole(Role)` | 2a | first role read in `BookSpace.Application` |
| Procedure-level concurrency tests | 1b | `CreateBookingProcedureTests`: 1-slot, 20-way, 2 pooled |

Four of WP-4's nine tasks and three of its four acceptance criteria are met. What
Phase 3 owes is **AC-1 at the HTTP level** — the one remaining criterion, and the
single most important test in the codebase (§8).

#### What Phase 3 has to do

1. **The HTTP-level concurrency suite.** Parallel `POST /bookings` through the
   real pipeline. Four cases, already specified in "Testing, mapped to the
   acceptance criteria" below and unchanged:
   - one slot, capacity 1, N simultaneous → exactly one 201, the rest 409
     `SlotUnavailable`, exactly one row, **and no 500s** (a deadlock escaping
     retry would surface here, and only here);
   - pooled: capacity 4, eight quantity-1 requests → exactly four succeed;
   - mixed quantities: capacity 4, one request for 3 and one for 2 → exactly one;
   - and worth adding, since 2b made it possible: a **cancel racing a create**
     for the freed slot, which the procedure-level tests could not express.
2. **Append the measured end-to-end evidence to `0023`.** The record already
   carries Phase 1b's numbers (lock hints removed → 3 bookings on a capacity-1
   room, a pool of 4 filled 10 times). Phase 3 adds the HTTP figures and, if the
   1205 retry fires, how often — `0023`'s claim is that the retry is *part of the
   design*, and Phase 1c already softened "blocks" to "usually blocks" after
   observing a deadlock at ten-way contention. More data is what makes that
   honest rather than defensive.
3. **Seed real bookings.** `SeedData` has stopped short of `Bookings` since WP-1
   because there was no legitimate write path (§4.1). There is one now and it is
   idempotent, so a handful become possible. **Read the seeding constraint first**:
   `SeedData` runs with no `HttpContext`, so `ICurrentTenant.OrgId` is null and it
   uses `TenantBypassScope` — but `dbo.CreateBooking` deliberately **fails closed
   without a tenant session context** (`0023`'s fail-open guard), so seeding
   through the procedure needs the session context set, not bypassed. That
   tension is unresolved and is the first thing to work out.
4. **Make WP-2's `Bookings` isolation test real.**
   `TenantIsolationTests.BookingsQueryFilter_WithNoTenantContext_ReturnsEmptyWithoutThrowing`
   is a smoke check with a comment saying so — it proves the filter clause builds,
   not that a cross-tenant read returns nothing, because no `Bookings` rows
   existed. Now they can. This is AC-4's last gap.
5. **The final AC sweep** — confirm all four acceptance criteria and tick WP-4 in
   CLAUDE.md §12.

#### Outstanding CLAUDE.md corrections

**Done in 3b, 2026-09-08** — kept below as the brief wrote it, since it is the
input this phase worked from. Nothing here is still outstanding.

One is **overdue and currently makes CLAUDE.md wrong about the code**:

- **§6's tier table still lists blackouts as tier 4 only.** Correction item 5
  below said this lands "in Phase 1b with the procedure"; the procedure shipped
  with the blackout re-check under the lock and the table was never updated. So
  §6 currently understates where that rule lives. Smaller call 3 settled the
  reasoning (decision `0001` gives a blackout absolute priority, and §6's own
  rule of thumb puts a "must never" in tiers 1–3), so this is a documentation fix
  with nothing left to decide — the tier-4 row keeps blackouts *and* tier 2 gains
  them, because both checks genuinely exist.

Everything else in "Corrections to CLAUDE.md that WP-4 forces" is done.

#### Things a fresh session should know before touching this

- **Decision `0023` is the reference for the concurrency design.** Read it before
  writing the tests — particularly the open lower bound on the range lock (creates
  on one resource serialize against each other whether or not their times
  overlap), which is why a 20-way test is slow rather than broken.
- **The 1205 retry is production behaviour, not a test workaround.** Phase 1c hit
  a deadlock at ten-way contention because the *procedure-level* test called the
  procedure over a raw connection with no execution strategy. Anything going
  through `POST /bookings` gets the retry via `IUnitOfWork`; anything calling the
  procedure directly must retry the way production does.
- **`member2@acme.test` is unusable in integration tests** —
  `AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin` deactivates it
  permanently. Use `approver@acme.test` as a second Acme account. A test using
  member2 passes alone and fails only in a full run, with a 401 on *login*.
- **The concurrency tests will need their own state hygiene.** Every booking test
  file creates its own resource and deletes it in a `finally`, in the order
  Notifications → ApprovalRequests → Bookings → Resources (§4.5's `NoAction`
  FKs), with an explicit RLS bypass on the fixture connection (decision `0017`'s
  gotcha). Copy that shape; a leaked resource breaks other files' counts.
- **Verify the tests can fail.** Every WP-4 chunk so far has: 1b removed the lock
  hints, 2a disabled the owner filter, 2b did both the owner filter and the
  notification suppression. For Phase 3 the equivalent is removing the hints again
  and confirming the *HTTP* suite fails, which is the end-to-end counterpart to
  1b's evidence. Never commit the weakened version.

#### Known loose ends Phase 3 does not own

Listed so they are not mistaken for Phase 3 work:

- **A cancelled `Pending` booking keeps its `ApprovalRequests` row at
  `Pending`.** Nothing withdraws it. **WP-5's** approve path must check the
  booking's status or an approver could approve a cancelled booking. Recorded in
  `0002`'s amendment.
- **The owner's name is not on the booking read DTOs**, only `UserId`. For
  `scope=tenant` an admin sees opaque GUIDs. Deliberately not built in 2a — the
  owner's call, and `ApproverDetail`'s id-and-name-no-email shape is the
  precedent if it is wanted.
- **`GET /bookings/{id}` carries no approval detail** for a `Pending` booking.
  WP-5 owns approvals and the wire shape belongs with its approver queue.
- **Reminder rows (FR-8.3) are not written by any booking path** — smaller call 7,
  deferred to the notifications package.
- **Nothing writes `BookingStatus.Completed`**, and that gap is now load-bearing
  in three places (`CanBeCancelledForBlackout`, `CanBeCancelled`, and the
  blackout cascade's reach). Worth raising with the mentor; it is in no current
  work package.
- **§9's DST fall-back question** belongs to WP-5, reassigned 2026-09-07.

#### Suggested chunking

Two chunks, matching the delivery style: **3a** the concurrency suite plus the
`0023` evidence — the acceptance-criterion work, defensible on its own; **3b**
the seeding, the real isolation test, the tier-table correction and the AC sweep,
which is closing-out work with no shared risk.

#### The build plan, as agreed 2026-09-08

Written from the brief above after reading `0023`, the procedure, the create
handler, `UnitOfWork`, the RLS predicate and interceptor, `SeedData` and the four
existing booking/isolation test files. Confirmed with the owner the same day.
**Nothing in production code changes in 3a**; 3b touches `SeedData` only.

Three findings the brief did not have:

1. **The seeding tension resolves — a bypass is not the fail-closed case.** The
   brief calls this unresolved. `Security.fn_TenantAccessPredicate` returns
   *allow* whenever `TenantBypass = 1`, and `TenantBypassScope` sets
   `TenantInit = 1` alongside it, so the procedure's `Resources` read succeeds
   inside a bypass scope; `0023`'s guard fires only on a connection with **no**
   session context at all. The counting `SELECT` is still correct under bypass
   because it filters by `ResourceId`, and `FK_Bookings_Resources_SameOrg`
   (decision `0006`) forces every booking of a resource into that resource's org,
   so there is nothing cross-tenant for it to over-count. One gotcha:
   `TenantSessionContextInterceptor` reads the flag at `ConnectionOpened` and
   sets its keys `@read_only = 1`, so the scope has to be entered *before* the
   connection opens.
2. **The handler's capacity pre-check is a confound the race must be designed
   around.** `CreateBookingCommandRequestHandler` checks capacity before the unit
   of work opens, so contenders that arrive *after* a winner has committed are
   refused by the pre-check and the lock is never contended — a trickling race
   would pass with the lock hints removed. The gate is what makes it a real race,
   and the weakened run is what proves so.
3. **Reason codes in a race are deterministic**, so the assertions can be exact
   rather than "one of". Capacity 1, N-way: losers get `SlotUnavailable`.
   Capacity 4 with 3 + 2: the loser always gets `CapacityExceeded`, whichever
   wins, since the remainder is 1 or 2 — both above zero and below the ask.
   Capacity 4 with eight 1-unit requests: losers get `SlotUnavailable`. And
   `CapacityExceededException.RemainingCapacity` is log-only, never on the wire
   (`0016`), so the two throw sites are indistinguishable to a client and no
   assertion can flake on which one answered.

##### 3a — the HTTP concurrency suite and `0023`'s end-to-end evidence

A new file, `Bookings/BookingConcurrencyEndpointTests.cs`, in
`AuthenticationTestCollection`, with the sibling files' state hygiene: its own
resource per test, removed in a `finally` in the order Notifications →
ApprovalRequests → Bookings → Resources with an explicit RLS bypass on the
fixture connection (decision `0017`'s gotcha). Its own file rather than an
addition to `CreateBookingEndpointTests`, whose header says the concurrency
criterion is deliberately not there; that comment is updated to point here.

`RaceAsync` mirrors `CreateBookingProcedureTests.RaceAsync`: **every access token
is minted before the gate**, then a `TaskCompletionSource` releases all racers at
once. Login is PBKDF2 at 100k iterations, so a login inside the race would
stagger the arrivals and hand the win to the pre-check rather than to the lock.

| Test | Setup | Asserts |
|---|---|---|
| `TwoSimultaneousRequestsForOneSlot…` | capacity 1, 2 requests | one 201, one 409 `SlotUnavailable`, one row |
| `TenSimultaneousRequestsForOneSlot…` | capacity 1, 10 requests | one 201, nine 409, one row |
| `ConcurrentRequestsFillAPoolExactly…` | capacity 4, eight 1-unit | four 201, four 409 `SlotUnavailable`, `SUM(Quantity) = 4` |
| `ConcurrentRequestsForMostOfAPool…` | capacity 4, one 3-unit + one 2-unit | one 201, loser 409 `CapacityExceeded` |
| `ConcurrentRequestsForDifferentHours…` | capacity 1, twelve hours | all 201 — the lock queues, never over-refuses |
| `CancelRacingACreate…` | capacity 1, cancel against create | the invariant, below |

Ten at HTTP level rather than the procedure suite's twenty: each request here is
a whole pipeline plus a transaction that serializes against the others, so twenty
would double the runtime to re-prove what `CreateBookingProcedureTests` already
proves at twenty. This file's subject is the pipeline, not the lock.

Two assertions exist only at this level:

- **No 500s, in every test** — every status must be 201 or 409. A deadlock that
  escapes the 1205 retry surfaces here and nowhere else, which is the one thing
  the procedure-level suite structurally cannot see.
- **The cancel/create race asserts an invariant, not an outcome.** The cancel is
  always 200; the create is 201 *or* 409 and both are correct, since the freed
  unit may or may not be visible yet. So what is asserted is: at most one live
  (`Pending`/`Confirmed`) booking overlapping the interval afterwards, the
  cancelled row still present, and no 500. It is also the most likely case to
  genuinely produce a 1205 — the cancel's EF `UPDATE` inverts lock order against
  the create's `RangeS-U` — which makes it the best available evidence for
  `0023`'s point 4.

Then an **Evidence (HTTP level)** subsection is appended to `0023`: the figures
with the hints and with them removed, and whether 1205 fired and how often. The
weakened procedure is produced by temporarily editing the
`AddCreateBookingProcedure` body — the test host drops and re-migrates its
database on every run, so editing the migration is the only way to get a weakened
procedure into it — reverted immediately and **never committed**, exactly as 1b
did it.

##### 3b — seeding, the real isolation test, the tier fix, the AC sweep

**Seed bookings.** `SeedData.SeedAsync` gains an `IBookingRepository` and calls
`CreateAsync` after the existing `SaveChangesAsync`, since the resources must
exist first, inside a `TenantBypassScope` per finding 1. Callers to update:
`Program.cs` and `AuthenticationTestHost.InitializeAsync`. Three things go stale
and are fixed in the same chunk: `SeedDataTests` asserts zero bookings,
`SeedData`'s header says it stops short of `Bookings` because the procedure does
not exist, and CLAUDE.md §12's WP-1 Notes say the same.

Shape, settled by the owner 2026-09-08: a **small mixed set anchored relative to
`now`** — per tenant, two `Confirmed` bookings on Conference Room A and one
`Pending` on the 3D Printer, the last carrying its `ApprovalRequest` row so WP-5's
approver queue has something to read on day one. Anchored to the next weekday at
10:00/11:00 **local**, so the dataset stays plausible however long after the seed
run it is demoed; a fixed absolute date was rejected for becoming silently
historical. The rows must satisfy the handler's rules **by construction** —
inside the seeded 09:00–17:00 weekday windows, within the 30–240 minute limits,
and in the future — because `SeedData` calls the repository rather than the
handler, so nothing checks availability, duration or elapsed-ness on its behalf.

**Make the `Bookings` isolation test real (AC-4's last gap).** Endpoint-level
cross-tenant booking reads are *already* covered — `BookingReadEndpointTests`'
`AdminScope_NeverReachesAnotherTenant` and its siblings, and
`BookingCancelEndpointTests.Cancel_RefusesAnAdminReachingIntoAnotherTenant`. What
is missing is only the **database/RLS half**, which is why this depends on the
seeding above rather than creating its own rows: it lets the new assertions look
exactly like the resources/users/windows/blackouts ones already in that file. Add
a `bookings/{id}` arm to `TenantIsolationProbeController`, filtered by `Id`
alone — the deliberately naive pre-D1 shape its blackout probe uses, safe only
because the query filter and RLS make it so — then in `TenantIsolationTests`: the
cross-tenant 404 / own-tenant 200 pair, a list test asserting Acme's seeded count
and every `OrgId`, `dbo.Bookings` added to the raw-connection zero-rows test and
to `RawConnection_WithFullSessionContext_SeesOnlyItsTenant`, and the smoke-check
comment deleted from `BookingsQueryFilter_WithNoTenantContext…`.

**CLAUDE.md §6's tier table** gains the blackout re-check on row 2 while row 4
keeps blackouts, because both checks genuinely exist (smaller call 3, `0023`).
This is the overdue correction the brief flags.

**The AC sweep**: tick WP-4's last task and AC-1, flip the WP-4 heading to Done,
and replace the handoff brief above with what actually happened, in the style
Phases 1 and 2 use.

##### Out of scope, flagged rather than done

- `docs/postman/README.md` is WP-3 only and a race is not hand-verifiable, so a
  WP-4 walkthrough is a separate ask.
- Drift spotted while reading: `ResourceReadEndpointTests`' header still
  describes Conference Room A as capacity 8, which decision `0005`'s amendment
  made 1. Comment only, no assertion depends on it; folded into 3b.
- The loose ends the brief lists — the cancelled `Pending` booking's approval
  row, owner names on the read DTOs, nothing writing `Completed`, the DST
  fall-back — stay with WP-5 and the mentor.

#### What Phase 3 actually produced

Delivered 2026-09-08 in two chunks, 3a then 3b, as planned. **879 unit + 406
integration tests pass** (from 395 at the end of Phase 2: +6 in 3a, +5 in 3b).
Every acceptance criterion is met and WP-4 is complete.

**3a — the concurrency suite.** `BookingConcurrencyEndpointTests`, six races,
exactly as tabulated in the plan. All six passed first time and were then shown
able to fail. The figures went into `0023`'s new HTTP-level Evidence section;
the headline is **ten confirmed bookings on a room that holds one** once the
lock hints come off, through the API a client actually calls.

Three things the plan did not anticipate:

- **Deadlocks are routine at this contention, not exotic.** Reading SQL Server's
  `_Total` deadlock counter either side of four runs *with* the lock in place
  gave **+0, +5, +1, +10** — sixteen across four runs of six tests, with no
  blackout written anywhere, so they are not the blackout/cascade inversion
  `0023`'s point 4 names. Every one was absorbed by the retry: all six tests
  passed in every run, and the only visible trace is wall clock, **17 seconds
  against 3**. That is the strongest evidence in the record for the retry being
  part of the design, and it is why "usually blocks" is the honest wording.
- **Two of the six tests cannot detect a missing lock**, and saying so in `0023`
  is what keeps the other four meaningful. Twelve requests at twelve different
  hours never contend for capacity — that test exists to show the range lock
  *queues* rather than refuses. The cancel/create race is the same: without the
  lock the challenger simply always wins, and the cancelled row plus the new one
  still leave exactly one live booking, so its invariant survives the weakening.
  Its value is the no-500 assertion and the lock-order inversion it exercises.
- **Ten contenders was the right number and for a reason the plan only guessed
  at.** It is past where `0023` first observed a 1205, which turns out to be
  where deadlocks become common rather than rare — so a smaller race would have
  measured the lock without ever measuring the retry.

**3b — seeding, isolation, and the corrections.**

- **`SeedData` writes `Bookings`**, through `dbo.CreateBooking` like every other
  write path. **The tension the brief flagged resolved rather than needing a
  workaround**: `TenantBypassScope` sets `TenantInit = 1` as well as
  `TenantBypass = 1`, and `fn_TenantAccessPredicate` allows every row on the
  bypass, so `0023`'s fail-closed guard is satisfied honestly — it fires on
  **no** session context, which a bypass is not. Two consequences worth
  recording: the connection is opened *inside* the scope, because the
  interceptor reads the flag at `ConnectionOpened` and sets its keys
  `@read_only = 1`; and the seed **throws** if the procedure refuses, because a
  refusal means the dataset contradicts the rules it is seeded through and a
  half-seeded database is harder to diagnose than none.
- **The seeded dates skip the seeded blackout**, which the plan missed. The
  procedure re-checks blackouts under the lock, so a booking on Christmas Day
  would be refused outright — the seed would have thrown for two days a year and
  worked for the other 363. `NextBookableLocalDate` skips weekends *and* any day
  the tenant's blackout covers, using the `BlackoutPeriod` already in hand
  rather than a query.
- **`SeedData` now truncates its clock to whole seconds**, the convention §4.3
  records for `IClock`. Not in the plan; noticed while writing the booking
  stamps, and it applies to every seeded row rather than just the new ones,
  since every instant column is `datetime2(0)` and *rounds* on write.
- **`SeedData.SeedAsync` gained two parameters** (`IBookingRepository`,
  `ITimeZoneCatalog`) rather than resolving them internally, so all three call
  sites — `Program.cs`, `AuthenticationTestHost`, `SeedDataTests` — say plainly
  that seeding now writes bookings through the §4.1 path. `SeedDataTests`
  constructs the real `BookingRepository` over its hand-built context for the
  same reason: a stub there would test nothing.
- **AC-4's last gap is closed.** `TenantIsolationTests`' single smoke check
  became four real tests over an `Id`-only probe arm, and the new tests were
  confirmed able to fail: bypassing *both* mechanisms in the probe makes the
  cross-tenant read return 200 while the own-tenant pair still passes, which is
  what makes the 404 isolation rather than a broken route.
- **§6's overdue tier-table correction landed**, with a paragraph under the table
  explaining why blackouts sit in two tiers and availability windows deliberately
  do not.
- One piece of drift fixed in passing: `ResourceReadEndpointTests`' header still
  described Conference Room A as capacity 8, from before decision `0005`'s
  amendment.

**What the seeded bookings do *not* include**, each for its own reason:
`Notifications` (nothing dispatches them yet, so rows would be permanently
unsent mail rather than realism), `RefreshTokens` (issued at login), and
occurrences for the seeded recurrence rule (materializing a series is WP-5's).

**Not done, and not Phase 3's**: the loose ends the brief lists are unchanged and
carry into WP-5 — the cancelled `Pending` booking that keeps its `ApprovalRequest`
at `Pending`, owner names absent from the read DTOs, no approval detail on
`GET /bookings/{id}`, no reminder rows, and nothing writing
`BookingStatus.Completed`. A WP-4 Postman walkthrough was also not written;
`docs/postman/README.md` remains WP-3's, and a race is not hand-verifiable.

---

## Testing, mapped to the acceptance criteria

**AC-1 — exactly one of two simultaneous requests succeeds.** The most important
test in the codebase (§8), at two levels:

- Procedure level (Phase 1b): parallel `SqlConnection`s calling
  `dbo.CreateBooking` for one slot on a capacity-1 resource. Exactly one
  `Created`, exactly one row.
- HTTP level (Phase 3): parallel `POST /bookings` through the real pipeline.
  Exactly one 201, the rest 409 `SlotUnavailable`, exactly one row, and **no
  500s** — a deadlock that escapes retry would surface here.
- Pooled (Phase 3): capacity 4, eight concurrent quantity-1 requests → exactly
  four succeed. This is the test that proves `0005`'s model rather than just
  mutual exclusion.
- Mixed quantities: capacity 4, one request for 3 and one for 2 → exactly one.

**All rule violations rejected with clear reasons.** Extends the existing
structured-error table (`ResourceWriteEndpointTests`' pattern): each new code
against the status its `ErrorKind` promises, every body a `ProblemDetails`
carrying the correlation id, and the exception message never on the wire.

**A member can cancel their own booking; the slot is freed.** Phase 2, above.

**Strategy documented and defended.** Decision `0023`.

Plus, per §8's standing list: the approval re-check (AC-5) is **WP-5's**, not
WP-4's, since `dbo.ApproveBooking` arrives with the approve endpoint.

---

## Smaller calls

The first five are **settled (owner, 2026-09-07)**; the rest are still open and
belong to the phase that hits them.

1. **`SlotUnavailable` vs `CapacityExceeded` — settled.** Both are declared, both
   are `Conflict`/409, and nothing said which applied when. The split is
   **nothing free at all → `SlotUnavailable`; some free but fewer than asked →
   `CapacityExceeded`.** On an exclusive resource only the first can occur, which
   reads correctly; on a pool, "you asked for 3 and 2 are left" is genuinely
   different information. Rejected: keying it off `Capacity == 1`, which would
   make the code describe the resource rather than the failure.
2. **`ApprovalRequired` is deleted — settled.** It has no thrower and, under the
   settled design, never will: FR-7.1 makes an approval-gated booking `Pending`
   rather than refusing it. `0016`'s own logic is that a code with no `sealed`
   subclass cannot be thrown at all, so leaving it in the catalogue would be an
   entry that lies about what the API can return. Removed from `ReasonCodes` and
   from CLAUDE.md §6's list; `ReasonCodesTests` needs no change beyond the
   deletion.
3. **The blackout re-check inside the procedure — settled, yes** (argued above).
   A deliberate move of one tier-4 rule into tier 2, justified by `0001`'s
   absolute priority and by §6's own rule of thumb. The lock-order inversion with
   the cascade is accepted: the two can deadlock, and with 1205 retry already
   configured, that deadlock *is* the serialization. Availability windows
   deliberately do not follow it — narrowing a window cancels nothing, so
   "inside a window" is not an invariant maintained after creation.
4. **Cancelling a booking that has already ended is refused**
   (`BookingNotCancellable`), mirroring `0019`'s `BlackoutPeriodElapsed` exactly:
   the test is on `EndsAtUtc`, so a meeting in progress is still cancellable.
   The alternative is to allow it and let history be rewritten.
5. **A booking whose interval is wholly in the past is refused — settled**
   (`BookingInThePast`); one that merely *starts* in the past is allowed — "book
   the room I am already sitting in". Taken without a separate question because
   it reapplies `0019`'s `BlackoutPeriodElapsed` precedent exactly: the test is
   on `EndsAtUtc`, deliberately not `StartsAtUtc`.
6. **Self-cancellation enqueues no `Cancelled` notification.** Decision 0002's
   requirement is that the *affected user* is told; emailing someone the news
   they just made is noise. A cancellation by anyone else does enqueue one.
7. **Reminder rows are not written by WP-4 — settled.** FR-8.3 needs a `Reminder` row per
   confirmed booking at `StartsAtUtc − ReminderLeadMinutes`, and booking creation
   is the only natural writer — but nothing dispatches them yet, and cancelling
   would then have to void them. Proposed: defer to the notifications package and
   record the dependency there rather than build half of it here.
8. **A non-admin passing `userId` on `GET /bookings` gets `ValidationFailed`
   400.** There is no `Forbidden` kind in `ErrorKind`, and inventing one for a
   query-string filter is heavier than the problem. The alternative is to add
   `ErrorKind.Forbidden → 403`, which is a one-arm change to `0016`'s map but a
   real widening of the error contract.
9. **Decision `0017`'s carve-out narrows rather than expires.** It says the
   raw-SQL booking fixture exists "because `dbo.CreateBooking` does not". Once it
   does, new tests use the real path, but the existing fixtures stay: they set up
   states the endpoint cannot produce (a `NoShow` row) or 260 rows where 260
   HTTP calls would dominate the run. Proposed as an amendment to `0017`, not a
   deletion.

---

## Decision records WP-4 will produce

- **`0023` — booking concurrency strategy**, written in Phase 1b beside the
  procedure it describes. Required by the fourth acceptance
  criterion. Contains the peak-vs-sum correction, the chosen range-lock design,
  the open lower bound and why it is not narrowed, the fail-open trap and its
  guard, and the four rejected alternatives with their reasons.
- **An amendment to `0002`**, covering what implementation forced the original to
  leave open: the cancellation window (`EndsAtUtc`), and the self-cancellation
  notification suppression.
- **An amendment to `0017`**, narrowing the test-fixture carve-out as above.

---

## Corrections to CLAUDE.md that WP-4 forces

1. ~~**§4.1's "sum of overlapping `Quantity`" is wrong**~~ — **done 2026-09-07**,
   before any code, as its own change so the hard rule could be reviewed on its
   own rather than buried in a phase. It now reads *peak concurrent*, with the
   counterexample recorded inline. The guarantee is unchanged; the arithmetic as
   written would have refused legal bookings on any pooled resource. This is the
   most consequential finding in the plan.
2. ~~**§9's still-open DST fall-back item is assigned to WP-4**~~ — **done
   2026-09-07**, reassigned to WP-5 with the reason: WP-4 creates one-off
   bookings from explicit UTC instants, so no ambiguous local time ever arises in
   it; the question is about a *recurring occurrence* resolving an ambiguous
   wall-clock time, and recurrence is WP-5's first task.
3. ~~**§12 gains WP-4 and WP-5 subsections**~~ — **done 2026-09-07**. WP-5 is
   tracked as its own subsection even though this plan does not cover it.
4. **§6's booking code list** gains the four new codes and loses
   `ApprovalRequired` (smaller call 2, settled). Lands in Phase 1a with the codes
   themselves.
5. ~~**§6's tier table** gains the blackout re-check as a tier-2 rule alongside
   its tier-4 entry~~ (smaller call 3, settled). Was to land in Phase 1b with the
   procedure and did not — **done 2026-09-08 in Phase 3b**, which is why the
   Phase 3 brief above flags it as overdue. The table now carries blackouts in
   both tiers, with a paragraph beneath it explaining why the duplication is the
   design and why availability windows deliberately do not follow.

---

## What WP-4 deliberately does not touch

- **Recurrence** — `RecurrenceRules` stays unwritten, `Bookings.RecurrenceRuleId`
  stays null. WP-5.
- **`dbo.ApproveBooking`, approve/reject endpoints, the approver queue, and the
  AC-5 re-check.** WP-5. WP-4 creates the `Pending` booking and the
  `ApprovalRequest` row those endpoints will decide.
- **Sending anything.** No email provider, no dispatch job. Rows only.
- **The three background jobs** (§7), including no-show release — so
  `Booking.MarkNoShow`, `IsNoShow` and `CheckIn` keep their zero production
  callers, and there is still **no writer for `BookingStatus.Completed`**. That
  gap is now load-bearing in two places (`CanBeCancelledForBlackout`, and the
  cancellation window above), which is worth raising even though fixing it is not
  in any current work package.
- **ICS feeds, utilization reporting, the frontend.**
