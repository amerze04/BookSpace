# WP-4 — Core Booking Engine: what was built and how to defend it

Written 2026-09-08, at the close of WP-4, as a review brief before the mentor
meeting. It explains the package in plain language and gives the reasoning
behind each choice, including the alternatives that were rejected and the
things that were found to be wrong along the way.

Sources, if a claim here needs chasing:
`docs/decisions/0023-booking-concurrency-strategy.md` (the concurrency design),
`docs/wp4-plan.md` (the build plan and every phase's outcome),
`CLAUDE.md` §4.1 / §6 / §12 (the hard rules and the roadmap).

**Final state:** all nine tasks done, all four acceptance criteria met,
**879 unit + 406 integration tests, 0 failed.**

---

## 1. What the package asked for

Build the booking engine: let a member create a one-off booking, refuse it with
a clear machine-readable reason when a rule says no, let them view and cancel
their own bookings — and make it **impossible** for two bookings to overlap on
the same resource beyond its capacity, *even when requests arrive at the same
instant*.

That is FR-4.1 to FR-4.5, and **AC-1**, the most important acceptance criterion
in the project. The work package's own words about it: "'it's unlikely' is not
an answer."

## 2. The one genuinely hard problem

Everything else in WP-4 is ordinary CRUD. This is not:

> Two people ask for the same room at the same moment. Both requests read the
> database, both see the room is free, both conclude there is room, both
> insert. The room is now double-booked.

This is the classic **check-then-act race**, and the naive fix — "check if it is
free, then save" — *is* the bug. The window between the check and the save is
where the second request slips in.

Three things you would normally reach for do not work here:

- **A unique constraint.** SQL Server can enforce "no two rows with the same
  value", but it cannot express "no two rows whose *time ranges overlap*".
  PostgreSQL has exclusion constraints for exactly this; SQL Server has nothing.
- **Optimistic concurrency (`RowVersion`).** That protects an *update* to a row
  that already exists. Two inserts contend over a row that does not exist yet,
  so there is no version to conflict on.
- **A lock in the application code** (a C# `lock`, a semaphore). Wrong layer,
  and false the moment two copies of the API run — which the Azure hosting
  target makes likely.

So the guarantee has to be built out of **database locks inside a transaction**.

## 3. The answer, in one paragraph

Booking creation goes through one stored procedure, `dbo.CreateBooking`. Inside
a single transaction it takes a **key-range lock** over the existing bookings
for that resource, works out how many units are actually in use at the busiest
instant of the requested interval, and inserts the row only if there is room.
Because the lock covers not just the rows that exist but the *gaps between
them*, a second transaction physically cannot insert into a range this one has
already counted. No application code can bypass it, because
`IBookingRepository` has no `Add(Booking)` method and never will — the interface
shape is CLAUDE.md §4.1 made structural rather than a comment asking people to
remember it.

## 4. How the lock works — four parts, each load-bearing

If the question is "why not just do X", the answer is usually one of these.

### `HOLDLOCK` — lock the gaps, not just the rows

The rows that break the guarantee are the ones that **do not exist yet**.
`HOLDLOCK` gives the query serializable semantics, so SQL Server locks the whole
**key range** the query scanned, empty space included. A second transaction
cannot insert into that space until the first commits. This is the part that
actually closes the race: it kills the *phantom row*.

### `UPDLOCK` — so contenders queue instead of deadlocking

With `HOLDLOCK` alone both transactions take *shared* range locks, which are
compatible with each other. So both pass the capacity check, and then both try
to insert into the other's locked range — and deadlock. Correct, but only via a
deadlock on every single pair, which is a terrible normal path. `UPDLOCK` makes
the locks **update-mode**, which are mutually incompatible, so the second
transaction blocks, waits, then reads the winner's committed row and refuses
honestly.

Be careful how you phrase this one: **it reduces deadlocks, it does not abolish
them.** That was measured (§8), and the decision record deliberately says
"usually blocks" for this reason rather than overclaiming.

### `IX_Bookings_Resource_Start` — the index is what makes the lock narrow

A range lock is only safe if the range is small. The query seeks on `ResourceId`
plus a time bound, so the lock covers one resource's slice of the index rather
than the whole table. The index also `INCLUDE`s `EndsAtUtc`, `Status` and
`Quantity`, so the check is answered from the index alone and never touches the
base table — a base-table lookup would widen the lock. This is why CLAUDE.md has
called that index "load-bearing" since WP-1 and says never to drop it.

### Retrying deadlocks is part of the design, not a safety net

EF Core is configured with
`EnableRetryOnFailure(5, 10s, errorNumbersToAdd: [1205])` — 1205 is SQL Server's
"you were chosen as the deadlock victim" — and `IUnitOfWork` runs the whole unit
of work inside `CreateExecutionStrategy().ExecuteAsync(...)`, so a victim is
replayed from the start.

This is needed because the blackout re-check reads `BlackoutPeriods` **after**
`Bookings`, while the blackout cancellation cascade from WP-3 writes them in the
**opposite order** — a genuine lock-order inversion. The deadlock it can produce
*is* the serialization between those two operations.

One consequence worth knowing: because the retry re-runs the whole block, **the
booking's `Guid` is chosen before the transaction opens**, and the approval and
notification rows are staged before it too. Otherwise a retry would mint a
second id and insert duplicates.

### The main alternative, and why rejecting it matters

The work package itself suggests **`sp_getapplock`** — a named application lock
keyed on the resource id. It is genuinely attractive: deadlock-free, independent
of the query plan, exactly per-resource. It was rejected because **the guarantee
would move off the data and into a string**. Nothing forces a future query to
take that lock before reading, and the failure would be silent. With a range
lock, the lock is taken *by the very query that does the checking* — so a code
path cannot skip the lock without also skipping the check. It is kept as the
documented fallback if deadlocks ever exceed what retry can absorb, and that
would be a measurement rather than a guess.

Also rejected: a filtered unique index (cannot express overlap), transaction-level
`SERIALIZABLE` (same locks, wider blast radius, no extra guarantee), and
narrowing the locked range with a lower bound like
`StartsAtUtc >= @start − MaxDurationMinutes` — that last one is a correctness
risk taken for a performance win, because a booking made before the limit
existed, or before it was lowered, would fall outside the bound and be **missed
entirely**, silently overbooking.

## 5. Two real bugs found in the project's own documentation

This is the part that shows judgment rather than typing.

### (a) CLAUDE.md §4.1 was wrong: "sum" should have been "peak concurrent"

From WP-1 until this package, the hard rule said to compare *the sum of
overlapping quantities* against capacity. That over-counts and refuses legal
bookings.

> Capacity 2. Existing bookings: 09:00–10:00 (1 unit) and 10:00–11:00 (1 unit).
> Someone asks for 09:30–10:30 (1 unit). It overlaps both, so the **sum** is 2,
> and `2 + 1 > 2` refuses. But those two bookings never coexist — at every
> instant of the request exactly one unit is held and one is free. The booking
> is legal.

Worse, the availability query built in WP-3 sweeps properly, so it **would have
offered that slot** and the booking would then have been refused — the API
contradicting itself, which is precisely the drift `AvailabilityCalculator` was
written to prevent.

The procedure computes the **peak**: the highest number of units in use at any
instant inside the request. Because the peak of a step function occurs at a
step, only two kinds of instant can hold it — the request's own start, and the
start of each overlapping booking inside the request. That is a small correlated
aggregate, not a full sweep, and it is the same arithmetic as `CapacitySweep` in
`BookSpace.Domain` reduced to one worst case. CLAUDE.md was corrected on its own,
before any code was written, so the hard rule could be reviewed in isolation
rather than buried in a phase.

### (b) The row-level-security policy had a fail-open hole

`Security.TenantAccessPolicy` is a **filter** policy: it filters `SELECT`s but
does **not** block `INSERT`s. So a connection with no tenant session context —
one that dodged the interceptor, or had `TenantInit` unset — would see **zero**
overlapping bookings, pass the capacity check trivially, and overbook. No lock
can prevent that, because the lock was correctly taken over an empty set.

The fix costs one index seek: the procedure reads the `Resources` row **through
the same filtered table** before counting anything, and refuses if the resource
is invisible. No session context means no `Resources` row either, so the worst
failure mode in the entire design degrades to a `ResourceNotFound` instead of a
silent double-booking. A test asserts both the result code and that no row was
written.

## 6. What the API does now

**`POST /bookings`** (any tenant role). Creates a booking — `Confirmed`
normally, `Pending` if the resource requires approval, in which case it also
writes the `ApprovalRequest` row an approver will later decide plus one
notification row per approver, all in the same transaction as the booking.
Refusals carry a reason code.

**`GET /bookings`** and **`GET /bookings/{id}`**. Paged list, chronological by
default. A member sees **their own** by default — and so does an admin; an admin
has to *ask* for more, via a `userId` filter or `scope=tenant`. WP-5's approver
queue will build on that scope.

**`POST /bookings/{id}/cancel`**. The owner can cancel, and per decision `0002`
so can a TenantAdmin over any booking in their tenant.

Three design points on the reads and the cancel worth being able to state:

- **The visibility filter is in the SQL query, never a comparison after the
  read.** `BookingReadRules` resolves a `BookingOwnerFilter` and the repository
  applies it as a `WHERE` clause, so a booking you may not see is *never
  materialized* and the handler's only branch is "not found". That is why an
  unreachable booking is **one indistinguishable 404, never a 403** — a 403
  would confirm the id exists, which AC-4 forbids. The filter is a named type
  rather than a nullable `Guid` precisely so the widened case cannot be reached
  by an omitted argument.
- **The cancellation window is `EndsAtUtc`, not `StartsAtUtc`.** A meeting in
  progress can still be called off; one that has ended cannot. This is
  load-bearing rather than tidy, because **nothing in this system writes
  `BookingStatus.Completed`** — an attended meeting is still `Confirmed`, so
  status alone would let a member rewrite history.
- **Cancelling is deliberately *not* a §4.1 write path.** §4.1 governs writes
  that *add* demand against capacity, because only those can breach it; a
  cancellation can only ever reduce the units held at an instant. So the cancel
  goes through EF and one `SaveChangesAsync` — no procedure, no lock, no
  capacity check. That contrast is what §5's `CreateExecutionStrategy` rule is
  actually about: mixing raw SQL with EF forces an explicit transaction, a pure
  EF unit of work does not. Concurrent cancels are handled by `RowVersion` → 409.

## 7. Where each rule lives, and why the apparent duplication is not duplication

### Capacity is checked twice, on purpose

The handler pre-checks it; the procedure checks it under the lock; **the
procedure's answer is authoritative.** Dropping the pre-check would still be
correct and would still never double-book — but then someone asking for a slot
that is plainly, obviously full would get the same bare 409 as someone who lost
a race by a millisecond, and the API would stop being able to explain itself
(FR-4.5). The pre-check reports what was true a moment ago; the procedure
decides.

Critically, the pre-check reads through `IAvailabilityRepository` — **the same
three queries the availability endpoint makes, feeding the same Domain code**.
That is not convenience: it is what makes "the endpoint offered me this slot"
and "the endpoint accepted my booking" the same question.

### Blackouts moved from one tier to two

CLAUDE.md §6 sorts rules by what enforces them: tier 1 database constraints,
tier 2 the locking protocol, tier 3 isolation, tier 4 application code.
Blackouts were tier 4. The procedure now re-checks them **under the lock**, so
they are tier 2 as well — and both entries are real.

Why: decision `0001` gives a blackout **absolute** priority (a live booking
inside one must never exist), and between the handler's check and the insert an
admin's cascade can select the bookings to cancel and **miss this one because it
does not exist yet**. §6's own rule of thumb puts a "must never" in tiers 1–3.

**Availability windows deliberately do not follow**, and the asymmetry is the
justification: narrowing an availability window cancels nothing, so "inside a
window" is not an invariant the system maintains after creation, whereas
"outside every blackout" is. Same for the duration limits — nobody but an admin
writes them, so they cannot race a booking.

That tier-table correction was **overdue**: the procedure shipped with the
blackout check in Phase 1b and the table was not updated until Phase 3. It is
fixed now, with the reasoning written under the table.

### Reason codes

Four new ones — `BookingNotFound`, `BookingNotCancellable`,
`BookingDurationOutOfRange`, `BookingInThePast` — and one **deleted**:
`ApprovalRequired`. It had been declared since FR-4.5 but can never be thrown,
because FR-7.1 says a booking on an approval-gated resource becomes `Pending`
rather than being refused. Decision `0016`'s whole premise is that the catalogue
describes what the API can actually return, so an entry nothing can throw is an
entry that lies.

**`SlotUnavailable` vs `CapacityExceeded`** are split by *what is left*, not by
the resource: nothing free at any instant → `SlotUnavailable`; something free
throughout but less than was asked for → `CapacityExceeded`. An exclusive
resource (capacity 1) can therefore only ever produce the first, which reads
correctly. Keying it off `Capacity == 1` was rejected, because that would make
the code describe the resource rather than the failure.

Every failure is a named `sealed` subclass of `AppException` that fixes its own
kind and code, and `GlobalExceptionHandler` maps kind → status **once**. The
exception message never reaches the wire; the reason code carries the meaning.

## 8. How it was proved — the strongest part of the package

**A concurrency test that has never failed proves nothing.** So every claim was
checked by breaking the thing it depends on and confirming the test goes red.
The weakened version was never committed.

**Two levels, deliberately.**

- *Procedure level*: parallel raw `SqlConnection`s calling `dbo.CreateBooking`
  directly. No Kestrel, no EF, no mediator — so a failure can only be the lock.
- *HTTP level*: parallel `POST /bookings` through the real pipeline. What this
  adds is the pipeline, not the guarantee — and one property only visible here:
  **every response must be 201 or 409**. A deadlock escaping the retry would
  surface as a 500 to a real client and as *nothing at all* to the
  procedure-level suite, which runs its own retry loop over a raw connection.

**The measured evidence — locks removed, nothing else changed:**

| Case | With the lock | Without it |
|---|---|---|
| 2 simultaneous, capacity 1 | 1 booking | **2** (every run) |
| 10 simultaneous, capacity 1 (HTTP) | 1 booking | **10, 8, 9** over three runs |
| 20 simultaneous, capacity 1 (procedure) | 1 booking | **3** |
| pool of 4, eight 1-unit requests | 4 | **5, 5, 6** |
| capacity 4, one 3-unit + one 2-unit | 1 | **both** — 5 units against a capacity of 4 |

Ten confirmed bookings on a room that holds one, through the API a client
actually calls. That is the number to quote.

**And the honest caveats, which are what make the rest credible:**

- **Two of the six HTTP tests cannot detect a missing lock, and `0023` says so.**
  Twelve requests at twelve *different* hours never contend — that test exists
  to prove the lock **queues** rather than refuses, and it would pass either
  way. The cancel-racing-a-create test is similar: without the lock the
  challenger simply always wins, and the cancelled row plus the new one still
  leave exactly one live booking, so its invariant survives the weakening. Its
  value is the no-500 assertion and the lock-order inversion it exercises.
- **Deadlocks are routine at this contention, not exotic.** Reading SQL Server's
  deadlock counter either side of four runs *with* the lock in place gave
  **+0, +5, +1, +10** — sixteen across four runs of six tests, with no blackout
  write involved anywhere. Every one was absorbed by the retry: all six tests
  passed in every run, and the only visible trace was wall clock, **17 seconds
  against 3**. That is the design claim measured rather than argued.

If a mentor says "so your design deadlocks?" — yes, and that is stated in the
record, retried by configuration that predates this package, and invisible to
clients. The strategy's correctness never depended on deadlocks being
impossible, only on their being retried.

### AC-4's last gap, closed

Since WP-2 the cross-tenant *bookings* isolation test had been a smoke check
with a comment admitting it: no bookings existed to leak, so all it proved was
that the filter clause compiled. The seed now writes six bookings, so it is four
real tests — cross-tenant 404 / own-tenant 200 over a probe that filters by id
alone, an exact-count list, and a raw connection seeing zero rows without a
session context and exactly Acme's three with it. Confirmed able to fail:
bypassing **both** isolation mechanisms makes the cross-tenant read return 200.

### The seed data finally contains bookings

Written through the same procedure, so the demo dataset shows what the engine
produces. Two details worth knowing: a `TenantBypassScope` sets `TenantInit = 1`,
so §5(b)'s fail-closed guard is satisfied **honestly** rather than dodged — it
fires on *no* session context, which a bypass is not; and the seeded dates skip
the seeded Christmas blackout, because the procedure would refuse a booking
inside it. Without that, seeding would have thrown for two days a year and
worked for the other 363.

## 9. What is written down, and what is still open

**Produced:** decision `0023` (the concurrency strategy, with the rejected
alternatives, the peak-vs-sum correction, the fail-open hole and its guard, and
all the measured evidence), an amendment to `0002` (the four cancellation
mechanics implementation forced), an amendment to `0017` (narrowing the raw-SQL
test-fixture carve-out), and corrections to CLAUDE.md §4.1, §6's code list and
§6's tier table.

**Deliberately not touched:** recurrence (WP-5), `dbo.ApproveBooking` and the
approve/reject endpoints (WP-5 — WP-4 creates the `Pending` booking and the
`ApprovalRequest` those will decide), actually *sending* anything (rows only; no
email provider, no dispatch job), and the three background jobs.

**Known loose ends, all flagged rather than hidden:**

- A cancelled `Pending` booking **keeps its `ApprovalRequests` row at
  `Pending`** — nothing withdraws it. WP-5's approve path must check the
  booking's status, or an approver could approve a cancelled booking. Raise this
  proactively; it is the sharpest gap in the package.
- **Nothing writes `BookingStatus.Completed`**, and that gap is now load-bearing
  in three places (the cancellation window, the blackout cascade's reach, and
  `CanBeCancelledForBlackout`). It is in **no** current work package — worth
  asking the mentor about directly.
- Read DTOs carry `UserId` but no owner *name*, so `scope=tenant` shows an admin
  opaque GUIDs. Deliberate, deferred.
- Reminder rows (FR-8.3) are not written by any booking path — deferred to the
  notifications package rather than half-built here.
- The DST **fall-back** question (a local time that occurs twice) was reassigned
  from WP-4 to WP-5, because WP-4 creates bookings from explicit UTC instants
  supplied by the client, so no ambiguous local time ever arises in it.
- `AI-USAGE.md` has no WP-3 or WP-4 entry. Its entries are first-person about
  what *you* reviewed and corrected, so they are yours to write; the factual
  half can be drafted from `docs/wp4-plan.md`.

## 10. The questions you will actually be asked

**"Why a stored procedure? Isn't that unfashionable?"**
Because the guarantee is `UPDLOCK, HOLDLOCK` range locks, and EF/LINQ cannot
express table hints. There is no exclusion constraint in SQL Server to lean on.
So this is the one place the guarantee can exist, and `IBookingRepository`
having no `Add(Booking)` makes that structural.

**"Why not `sp_getapplock`?"**
It moves the guarantee off the data and into a string that nothing forces a
caller to take, and the failure is silent. A range lock is taken by the query
that does the checking. Kept as the documented fallback if measurement ever
demands it.

**"Doesn't the lock serialize everything?"**
Concurrent creates *on one resource* serialize, yes — an accepted consequence,
with a test proving twelve bookings at twelve different hours all succeed, so it
delays rather than refuses. Narrowing the range with a lower bound was
deliberately declined as a correctness risk taken for a performance win.
Contention is per resource, never table-wide.

**"How do you know it works?"**
Two independent levels, both shown able to fail by removing the lock hints and
nothing else — ten bookings on a one-person room. Plus measured deadlock counts
proving the retry does its job.

**"Why check capacity in two places?"**
Correctness comes from the procedure; the *explanation* comes from the
pre-check. Without it, FR-4.5's "clear, machine-readable reason" degrades to one
undifferentiated 409.

**"Where is the weakest point?"**
Two candidates, both documented: the cancelled-`Pending` approval row that WP-5
must handle, and the fact that nothing writes `Completed`, which three separate
rules now quietly depend on.
