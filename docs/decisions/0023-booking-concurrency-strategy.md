# 0023 — Booking concurrency strategy

## Status
Decided (2026-09-07), implemented the same day in WP-4 Phase 1b
(`AddCreateBookingProcedure`).

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

**2. `UPDLOCK` — U-mode, so contenders block instead of deadlocking.** With
`HOLDLOCK` alone the two transactions take compatible `RangeS-S` locks, both
pass the capacity check, and then deadlock when each tries to insert into the
other's locked range. That is *correct* — one dies as victim 1205 and retries —
but it makes a deadlock the normal path under contention. `UPDLOCK` makes them
`RangeS-U`, which are mutually incompatible, so the second transaction waits,
then reads the winner's committed row and refuses honestly.

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

## Notes

The work package asks for the failing test to be written first and watched to
fail. That was not possible as written: CLAUDE.md §4.1 forbids inserting
`Bookings` through LINQ or `SaveChanges`, so no naive path could legitimately
exist even temporarily, and the owner's call (2026-09-07) was to build the
procedure correct from its first migration. The Evidence section above is what
replaces the demonstration, and it is arguably stronger: it shows the tests
failing against a version of *this* procedure with one thing removed, rather than
against a different implementation nobody would have shipped.
