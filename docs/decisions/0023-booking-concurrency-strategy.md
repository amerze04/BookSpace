# 0023 — Booking concurrency strategy

## Status
Decided (2026-09-07), implemented the same day in WP-4 Phase 1b
(`AddCreateBookingProcedure`). **Evidence extended 2026-09-08** (WP-4 Phase 3)
with the HTTP-level figures and the measured deadlock/retry data.
**Evidence extended again 2026-09-09** (WP-5 Phase 4) with `dbo.ApproveBooking`
inheriting the strategy: the same weakening technique confirms its distinct
race (two decisions on one row, not two inserts into a range) actually needs
the lock, and the same deadlock-counter method finds contention even more
routine here than at create time.
**Amended 2026-09-15** (hardening pass, items 3/4 from an external review):
both procedures now hold `WITH (HOLDLOCK)` on their `Resources` read too — see
below.

## Context

FR-4.2: "the system must prevent two confirmed bookings from overlapping on the
same resource beyond its capacity — **guaranteed under concurrent requests**."
AC-1 states the test: given one remaining slot and two simultaneous requests,
exactly one succeeds and the other gets a clear rejection, never both. WP-4's
work package names it the hard problem and says "'it's unlikely' is not an
answer."

"Check if it is free, then save" fails here in the ordinary way. Two requests
read the same empty range, both conclude there is room, and both insert. SQL
Server has no exclusion constraint to lean on, so nothing in the schema can
express "these two rows may not both exist", and the guarantee has to be built
out of locks inside a transaction.

Two facts about this system shape the answer:

- **Capacity counts concurrent units** (`0005`), so the question is not "is
  there a row here" but "does the peak of overlapping quantity, plus mine,
  exceed the capacity".
- **`Bookings.RowVersion` is no help.** Optimistic concurrency protects an
  update to a row that already exists; two inserts contend over no row at all.

## Decision

Booking creation goes through **`dbo.CreateBooking`**, which inside one
transaction takes **`UPDLOCK, HOLDLOCK` key-range locks** on the overlapping
bookings for that resource, computes the **peak concurrent quantity** across the
requested interval, and inserts only if `peak + quantity <= capacity`.

Four parts, each load-bearing.

**1. `HOLDLOCK` — range locks, not row locks.** The rows that break the
guarantee are the ones that do not exist yet. `HOLDLOCK` gives the statement
serializable semantics, so SQL Server locks the *key range* the seek covers,
gaps included, and a second transaction cannot insert into a range this one has
already counted.

**2. `UPDLOCK` — U-mode, so contenders usually block instead of deadlocking.**
With `HOLDLOCK` alone the two transactions take compatible `RangeS-S` locks, both
pass the capacity check, and then deadlock when each tries to insert into the
other's locked range — so a deadlock would be the *normal* path for every pair.
`UPDLOCK` makes them `RangeS-U`, which are mutually incompatible, so the second
transaction waits, then reads the winner's committed row and refuses honestly.

**It reduces deadlocks; it does not abolish them** — a correction to this
record's first draft, made the same day on observed evidence. A 1205 was seen at
ten-way contention on one slot during a full-suite run, with no concurrent
blackout write involved. Ordinary two-way contention blocks cleanly, which is
what `UPDLOCK` buys, but higher contention still finds cycles, and the blackout
re-check inverts lock order against decision `0001`'s cascade besides. This is
why point 4 is part of the design rather than a safety net, and why anything
calling the procedure without a retry — including a test — is running a
configuration production does not have.

**3. `IX_Bookings_Resource_Start` is what keeps the range narrow.** The seek is
`ResourceId = @r AND StartsAtUtc < @EndsAtUtc`, so the lock covers one
resource's key range rather than the table. The index `INCLUDE`s `EndsAtUtc`,
`Status` and `Quantity`, so the check is covered and no base-table lookup widens
the lock. CLAUDE.md §4.1 has called this index load-bearing since WP-1; this is
the sentence it meant.

**4. Retry on 1205 is part of the design, not a safety net.** EF Core's
`EnableRetryOnFailure(5, 10s, errorNumbersToAdd: [1205])` has been configured
since WP-2, and `IUnitOfWork` runs the whole unit of work inside
`CreateExecutionStrategy().ExecuteAsync(...)` so a victim is replayed as a whole.
It is needed because the blackout re-check (below) reads `BlackoutPeriods` after
`Bookings`, while the blackout cascade writes them in the opposite order — a
genuine lock-order inversion, and the deadlock it can produce *is* the
serialization between the two.

### Peak concurrent, not the sum of overlapping quantities

CLAUDE.md §4.1 described the check as comparing "the **sum** of overlapping
`Quantity`" against capacity from WP-1 until this record. That is wrong, and it
was corrected the same day.

Capacity 2. Existing bookings `09:00–10:00` (1 unit) and `10:00–11:00` (1 unit).
A request for `09:30–10:30` of 1 unit overlaps both, so the sum is 2 and
`2 + 1 > 2` refuses it. But the two never coexist: at every instant of the
request exactly one unit is held, one is free, and the booking is legal. The
availability query — which sweeps properly (`CapacitySweep`) — would already have
offered that slot, so the sum would produce exactly the drift
`AvailabilityCalculator`'s header exists to prevent: **the API offers a slot and
then refuses to book it**.

The peak of a step function occurs at a step, so only two kinds of instant can
hold it: the request's own start, and the start of each overlapping booking
inside the request. That is a small correlated aggregate, not a real sweep, and
it is the same arithmetic as `CapacitySweep` in `Domain` and
`PeakConcurrentBookedQuantityAsync` in the resource repository, reduced to one
worst case. Regression test:
`CreateBooking_MeasuresThePeakNotTheSumOfOverlappingBookings`.

### The fail-open trap, and the guard for it

`Security.TenantAccessPolicy` is a **filter** policy (`0013`): it filters the
procedure's `SELECT`s but does not block its `INSERT`. So a connection with no
tenant session context — an unset `TenantInit`, a connection that dodged
`TenantSessionContextInterceptor` — sees **zero** overlapping bookings, passes
the capacity check trivially, and overbooks. No lock can prevent that, because
the lock was correctly taken over an empty set.

The procedure therefore reads `Resources` through the same filtered table before
counting anything, and refuses when the resource is invisible. No session
context means no `Resources` row either, so the worst failure mode in the design
becomes a `ResourceNotFound` instead of a silent double-booking. It costs one
index seek. Test:
`CreateBooking_WithNoTenantSessionContext_FailsClosed`, which asserts the result
code *and* that no row was written.

### What else the procedure checks, and what it deliberately does not

Under the lock: **capacity**, and **blackouts**. The blackout re-check is a
deliberate move of one tier-4 rule into tier 2 (owner's call, 2026-09-07),
because `0001` gives a blackout absolute priority — a live booking inside one
must never exist — and between the handler's check and the insert, an admin's
cascade can select the bookings to cancel and miss this one because it does not
exist yet. §6's own rule of thumb puts a "must never" in tiers 1–3.

Not under the lock: **availability windows** and the **duration limits**.
Neither input is written by bookers, so neither races with a booking, and
widening the lock would buy no guarantee. The asymmetry with blackouts is the
justification: narrowing a window cancels nothing, so "inside a window" is not
an invariant this system maintains after creation, while "outside every
blackout" is.

## Evidence

### At the procedure level (2026-09-07, Phase 1b)

Measured on 2026-09-07 by removing `WITH (UPDLOCK, HOLDLOCK)` from the procedure
and running the same suite against the weakened version. **Nothing else
changed**, and the naive procedure was never committed or shipped — the point
was to confirm the tests can actually fail, since a concurrency test that has
never failed proves nothing.

| Test | With the lock | Without it |
|---|---|---|
| 2 simultaneous requests, capacity 1 | 1 created | more than 1 created |
| 20 simultaneous requests, capacity 1 | 1 created | **3 created** |
| 10 concurrent 1-unit requests, capacity 4 | 4 created | **10 created** |
| 2 concurrent 3-unit requests, capacity 4 | 1 created | both created |

Three confirmed bookings on a room that holds one, and a pool of four sold ten
times. The other 16 tests in the file passed either way, which is what confirms
the four are measuring the lock and not something else.

**A deadlock was also observed**, in the ten-way pooled race during a full-suite
run with the lock in place. It is recorded here rather than tidied away, because
it is the evidence for point 4: the strategy's correctness does not depend on
deadlocks being impossible, only on their being retried. Production retries
through `IUnitOfWork`; the procedure-level test now retries the same way and for
the same reason, since a raw connection with no execution strategy is a
configuration the application never runs in.

### At the HTTP level (2026-09-08, Phase 3)

The same method one layer up, and the end-to-end half of AC-1:
`BookingConcurrencyEndpointTests` fires parallel `POST /bookings` through the
real pipeline — test host, JwtBearer handler, mediator, EF transaction and
execution strategy — against a resource it creates itself. The weakened
procedure was produced the same way and reverted immediately; because the test
host drops and re-migrates its database on every run, editing
`AddCreateBookingProcedure`'s body is the only way to get a weakened procedure
into it. Three weakened runs rather than one, since every figure below is a
race and a single sample would understate the spread.

| Test | With the lock | Without it (three runs) |
|---|---|---|
| 2 simultaneous requests, capacity 1 | 1 created | **2, 2, 2** |
| 10 simultaneous requests, capacity 1 | 1 created | **10, 8, 9** |
| 8 concurrent 1-unit requests, capacity 4 | 4 created | **5, 5, 6** |
| one 3-unit + one 2-unit request, capacity 4 | 1 created | **both**, 5 units against a capacity of 4 |
| 12 concurrent requests at 12 different hours | all 12 created | all 12 created |
| a cancel racing a create for the freed slot | invariant holds | invariant holds |

Ten confirmed bookings on a room that holds one, through the API a client
actually calls. Every run with the lock in place was 6 of 6 green.

**Two rows deliberately do not measure the lock**, and saying so is what keeps
the other four meaningful. Twelve requests at twelve different hours never
contend for capacity at all — that row exists to show the range lock *queues*
rather than refuses (the open lower bound under "Alternatives considered"), and
it would pass either way. The cancel/create race is the same: without the lock
the challenger simply always wins, and the cancelled row plus the new one still
leave exactly one live booking, so its invariant survives the weakening. Its
value is the no-500 assertion and the lock-order inversion it exercises, not
detection of a missing lock.

**1205 fires in the production configuration, and the retry absorbs it.** Four
runs with the lock in place, reading SQL Server's `_Total` deadlock counter
either side of each:

| Run | Deadlocks | Result | Wall clock |
|---|---|---|---|
| 1 | 0 | 6 of 6 passed | 3 s |
| 2 | +5 | 6 of 6 passed | 17 s |
| 3 | +1 | 6 of 6 passed | 4 s |
| 4 | +10 | 6 of 6 passed | 17 s |

This is point 4 measured rather than argued, and it sharpens the correction this
record already made to its own first draft. Three things follow. **Deadlocks are
routine at this contention, not exotic** — sixteen across four runs of six
tests. **They are not confined to the blackout/cascade inversion point 4
names**: no blackout was written during any of these runs, so ten- and
twelve-way contention finds cycles of its own, which is why "usually blocks" is
the honest wording rather than "blocks". And **the retry is the whole
difference**: every deadlock was absorbed, no test failed, and the only place a
1205 is visible at all is the wall clock — 17 seconds against 3, the execution
strategy's backoff and nothing else. The suite's own guard is that every
response must be 201 or 409, so a 1205 that escaped the retry would fail a test
rather than slow one down.
### At the procedure level, for `dbo.ApproveBooking` (2026-09-09, WP-5 Phase 4)

`dbo.ApproveBooking` inherits this record's strategy whole (§ above), but it
guards a genuinely different race than a create does: two *decisions* on one
existing row (`WITH (UPDLOCK)`, a point lookup by primary key), not two
*inserts* into a shared range. Worth measuring separately rather than assumed,
the same reasoning that made WP-4 measure at two levels rather than one.

Measured the same way as Phase 1b's: removing both hints
(`WITH (UPDLOCK)` on the point lookup, `WITH (UPDLOCK, HOLDLOCK)` on the
overlap read) from `AddApproveBookingProcedure`'s migration body — never
committed or shipped — and running `ApproveBookingProcedureTests`' full 14
tests against the weakened, freshly-migrated database.

| Test | With the lock | Without it |
|---|---|---|
| 2 simultaneous decisions on one booking | 1 Approved | **2 Approved** |
| 10 simultaneous decisions on one booking | 1 Approved | **7 Approved** |

Two approvers deciding the same booking at the same instant both confirmed it,
and seven of ten simultaneous decisions on one booking all reported Approved —
exactly the failure mode the `UPDLOCK` point lookup exists to rule out. The
other 12 tests in the file passed either way, including
`ApproveRacingAConcurrentCancelOfTheSameBooking_TheyNeverBothWin` and
`ConcurrentApprovalsOfDifferentPendingBookingsExactlyFillingAPool_AllSucceed` —
recorded rather than tidied away, on the same reasoning §"At the HTTP level"
gives for its own two non-detecting rows: a lock-order race can pass by timing
even when genuinely unsafe, so a single weakened run not failing there is not
evidence the guarantee is unneeded, only that this run didn't happen to hit the
interleaving. The two same-row tests are the ones this weakening reliably
defeats, which is what makes them the meaningful pair.

**1205 fires on every single run here, more consistently than at create
time.** Four runs with the lock in place, reading the same `_Total` deadlock
counter either side of each:

| Run | Deadlocks | Result | Wall clock |
|---|---|---|---|
| 1 | +8 | 14 of 14 passed | 16 s |
| 2 | +5 | 14 of 14 passed | 16 s |
| 3 | +5 | 14 of 14 passed | 15 s |
| 4 | +5 | 14 of 14 passed | 17 s |

Twenty-three deadlocks across four runs of fourteen tests, **every run
producing at least five** — where Phase 1b's four `dbo.CreateBooking` runs
included one with zero. The weakened run above took **3 seconds** for the same
14 tests (no blocking to wait on, hence the smaller counts it produced instead
of the correct rejection); the correct, locked version takes five to six times
as long, for the same reason WP-4's HTTP-level runs did — the difference is
retry backoff, not correctness. Every one of the twenty-three was absorbed:
all four runs were 14 of 14 green, and the suite's own guard (an explicit
`Assert.Equal` on the exact outcome distribution, not a bare success check)
would have caught a 1205 that escaped `IUnitOfWork`'s retry.

The likely reason contention is *more* consistent here than at create time:
`TenSimultaneousApprovalsOfTheSameBooking_ExactlyOneSucceeds` and
`ConcurrentApprovalsOfDifferentPendingBookingsExactlyFillingAPool_AllSucceed`
both start every racer from the same `TaskCompletionSource` gate, same as
`dbo.CreateBooking`'s own ten-way test — but the point-lookup lock here is
narrower than a range lock, so contenders queue tightly on one row with
essentially nothing to interleave around, which turns out to produce a
conflict on this configuration on every run rather than most of them.

## Alternatives considered

**`sp_getapplock` keyed on the resource id.** The work package names it first,
and it is genuinely attractive: deadlock-free, independent of the query plan, and
it gives exactly per-resource serialization. Rejected because the guarantee moves
*off the data and into a string*. Nothing forces a future statement to take the
lock before reading, and the failure is silent — whereas a range lock is taken by
the very query that does the checking, so a code path cannot skip the lock
without also skipping the check. It also serializes a resource unconditionally,
including provably non-overlapping times. Kept as the documented fallback if the
range lock ever proves to deadlock more than retry can absorb: that would be a
measurement, not a guess.

**A filtered unique index.** Cannot express overlap. It solves "one booking per
exact start instant per resource", which is neither what capacity means (`0005`)
nor what FR-4.2 asks. The work package says as much.

**`SERIALIZABLE` at the transaction level instead of table hints.** Same locks,
wider blast radius: it would raise every read in the transaction, including the
resource lookup and anything the handler does afterwards in the same unit of
work, for no additional guarantee.

**Optimistic concurrency on `RowVersion`.** Protects updates to an existing row.
Two inserts contend over no row.

**Application-level locking** (a semaphore, a distributed lock). Wrong layer, and
false the moment there are two API instances — which the Azure hosting target
makes likely.

**Narrowing the locked range with a lower bound** (`StartsAtUtc >= @StartsAtUtc −
MaxDurationMinutes`). Rejected as a correctness risk taken for a performance win.
A booking made before the limit existed, or before it was lowered, would fall
outside the bound and be missed entirely — silently overbooking. The open lower
bound means concurrent creates on one resource effectively serialize whether or
not their times overlap; that is accepted, because contention is per resource
and never table-wide.

## Consequences

- **`dbo.CreateBooking` is the only way a booking may be created**, which was
  already CLAUDE.md §4.1 and is now enforceable rather than aspirational:
  `IBookingRepository` has no `Add(Booking)` and never will.
- **Concurrent creates on one resource serialize.** Under heavy contention on a
  single popular resource, requests queue rather than fail. Twelve concurrent
  bookings at twelve different hours all succeed
  (`ConcurrentRequestsForDifferentHoursAllSucceed`), so the lock does not
  over-refuse — it delays.
- **1205 is an expected outcome**, absorbed by the execution strategy. Any code
  path that calls the procedure must therefore be inside `IUnitOfWork`, and its
  delegate must be safe to run twice — which is why the booking's `Guid` is
  chosen before the unit of work starts.
- **CLAUDE.md §4.1 was corrected** from "sum" to "peak concurrent". The
  guarantee it describes is unchanged.
- **WP-5's `dbo.ApproveBooking` inherits all of this.** FR-7.5 and AC-5 need the
  same check at approval time, so it takes the same locks in the same order over
  the same index.
- **Amended 2026-09-15**: both procedures now also hold `WITH (HOLDLOCK)` on
  their `Resources` read, closing a gap an external review raised (items 3 and
  4): an `Archive`/`RequiresApproval` UPDATE, or a `ResourceApprovers` DELETE,
  could previously commit *after* the procedure had already read the row it
  based its decision on but *before* its own transaction committed — a plain
  shared lock, held to end-of-transaction, blocks that write until this one is
  done. This introduces one more realistic ABBA deadlock shape than existed
  before: `dbo.ApproveBooking` now takes `S(ResourceApprovers)` then
  `S(Resources)`, while a concurrent `ReplaceApprovers`/`SetRequiresApproval`
  save (which bumps `Resources.RowVersion`, see below) can hold `X(Resources)`
  waiting on `X(ResourceApprovers)` — the opposite order. Not measured the way
  the rest of this record's deadlock figures are (that would need its own
  weakened-procedure run); reasoned to be the same shape as the blackout-cascade
  deadlock this record already documents, and absorbed the same way: SQL
  Server's detector kills one side, the existing 1205 retry the whole
  design already depends on picks it back up. No test asserts the deadlock
  itself for this one — only that the invariant it could otherwise violate
  holds (`ResourceConcurrencyTests`).
- **`Resources.RowVersion`, added 2026-09-15** (hardening pass, item 2):
  closes a *different* race the same review raised — `UpdateResource` and
  `ReplaceApprovers` each read-check-mutate-save independently, so two
  concurrent requests could each see FR-3.3's invariant
  (`RequiresApproval ⇒ at least one approver`) satisfied against the *other's*
  about-to-be-superseded state and both commit, landing on exactly the state
  the invariant forbids. Ordinary optimistic concurrency, the same mechanism
  `Bookings.RowVersion` already uses: whichever request saves second gets
  `DbUpdateConcurrencyException` (409), because `ReplaceApprovers` already
  calls `Touch()` on the owning `Resource` even though its own changes are to
  the owned `ResourceApprovers` collection. Proved deterministically —
  `ResourceConcurrencyTests`, two `DbContext`s loading the same row and saving
  in the order that actually produces the race, per CLAUDE.md §8's preference
  for that over a timing-based test — rather than by firing concurrent HTTP
  requests and hoping they interleave.

## Notes

The work package asks for the failing test to be written first and watched to
fail. That was not possible as written: CLAUDE.md §4.1 forbids inserting
`Bookings` through LINQ or `SaveChanges`, so no naive path could legitimately
exist even temporarily, and the owner's call (2026-09-07) was to build the
procedure correct from its first migration. The Evidence section above is what
replaces the demonstration, and it is arguably stronger: it shows the tests
failing against a version of *this* procedure with one thing removed, rather than
against a different implementation nobody would have shipped.
