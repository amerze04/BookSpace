_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-4 — Core Booking Engine — **Done** (2026-09-08)

**All three phases complete, all four acceptance criteria met.** Phase 1
(create), Phase 2 (view/cancel) and Phase 3 (the HTTP-level concurrency proof,
seeded bookings, AC-4's last gap) all landed on 2026-09-07/08. Final test
baseline: **879 unit + 406 integration, 0 failed**.

Phase 3's own outcomes, beyond ticking AC-1:
- `BookingConcurrencyEndpointTests` — six races through the real pipeline. Its
  distinctive assertion is **every response is 201 or 409**, which is how a 1205
  escaping `IUnitOfWork`'s retry would be caught; the procedure-level suite
  cannot see that, because it runs its own retry over a raw connection.
- **`0023` gained measured HTTP figures**, including the discovery that
  **deadlocks are routine rather than exotic** at ten-way contention — sixteen
  across four runs, with no blackout write involved, every one absorbed by the
  retry, visible only as wall clock (17s against 3s). That is the record's
  point 4 measured rather than argued, and the reason its "usually blocks"
  wording is the honest one.
- **`SeedData` finally writes `Bookings`**, through `dbo.CreateBooking` like
  every other write path, so the demo dataset contains what the engine produces.
  The tension the plan flagged resolved rather than needing a workaround: a
  `TenantBypassScope` sets `TenantInit = 1`, so `0023`'s fail-closed guard is
  satisfied honestly — that guard fires on **no** session context, not on a
  bypass.
- §6's tier table is corrected: blackouts sit in **both** tier 2 and tier 4, and
  the paragraph under the table says why the duplication is the design.
- Decision `0017` gained the amendment WP-4 promised: the raw-SQL test-fixture
  carve-out **narrows rather than expires**.

**[`docs/wp4-defense.md`](docs/wp4-defense.md)** explains the whole package in
plain language for the mentor review — the race and why the obvious fixes fail,
the four parts of the lock, the two bugs found in this file, where each rule
lives, the measured evidence with its caveats, and the questions likely to be
asked with their answers. Written 2026-09-08.

Source doc: `docs/Work Packages - Week 4.pdf` (weeks 3–4, backend track), which
carries WP-4 and WP-5 together. Plan: `docs/wp4-plan.md`, drafted 2026-09-07
before any code, on four shape answers from the owner the same day.

Tasks — Week 3 (correct for a single user):
- [x] Create a one-off booking for an available slot. FR-4.1. **Done 2026-09-07**
      (Phase 1): POST /bookings on TenantMember, Confirmed or Pending.
- [x] Reject bookings outside availability, inside blackout, or over capacity.
      FR-4.3. **Done 2026-09-07** (Phase 1).
- [x] Return a clear, machine-readable reason on rejection. FR-4.5.
      **Done 2026-09-07** (Phase 1).
- [x] Let a member view and cancel their own bookings. FR-4.4. **Done
      2026-09-08** (Phase 2). Reads (2a): `GET /bookings` (paged; own by default,
      with an admin-only `userId` filter and tenant-wide `scope`) and
      `GET /bookings/{id}`. Cancel (2b): `POST /bookings/{id}/cancel`, for the
      owner and — per [`0002`](docs/decisions/0002-tenant-admin-cancellation.md)
      — for a TenantAdmin over any booking in their tenant. All three on
      `TenantMember`; a booking the caller may not reach is one
      indistinguishable 404, never a 403.
- [x] Write tests for the single-user happy path and each rejection reason.
      **Done 2026-09-08.** Every booking reason code has a row on the structured
      -error table, at the status its `ErrorKind` promises.

Tasks — Week 4 (correct under concurrency):
- [x] Write a test that fires two bookings for the same slot simultaneously.
      **Done 2026-09-07** (Phase 1b), at the procedure level over parallel raw
      connections, plus a 20-way and two pooled variants.
- [x] Choose and implement a concurrency strategy that makes a double-booking
      impossible. FR-4.2. **Done 2026-09-07** (Phase 1b): `dbo.CreateBooking`
      takes UPDLOCK/HOLDLOCK key-range locks and compares the peak concurrent
      quantity against capacity. Documented and defended in
      [`0023`](docs/decisions/0023-booking-concurrency-strategy.md); the
      remaining Week-4 tasks are the HTTP-level proof and the AC sweep.
- [x] Prove the fix with a concurrent test that passes. **Done 2026-09-08**
      (Phase 3), at both levels and both shown able to fail. Procedure level
      (1b): parallel raw connections; removing the lock hints gave three
      bookings on a capacity-1 room and a pool of four filled ten times. HTTP
      level (3): `BookingConcurrencyEndpointTests`, six races through the real
      pipeline; removing the same hints gave **ten** confirmed bookings on a
      room that holds one, twice over two more runs (8 and 9), a pool of four
      sold five and six times, and both of two unequal claims accepted. Figures
      in [`0023`](docs/decisions/0023-booking-concurrency-strategy.md).
- [x] Document which strategy was chosen and why. **Done 2026-09-07**:
      [`0023`](docs/decisions/0023-booking-concurrency-strategy.md).

Acceptance criteria:
- [x] Given one remaining slot and two simultaneous requests, exactly one
      succeeds and the other gets a clear rejection — never both. AC-1.
      **Met 2026-09-08** (Phase 3), at the procedure level and through the real
      HTTP pipeline. The criterion's literal case — two different members, one
      slot — is `TwoSimultaneousRequestsForOneSlot_ExactlyOneSucceeds` in both
      suites; the HTTP file also widens it to ten contenders, fills a pool of
      four to exactly four, refuses the loser of two unequal claims with
      `CapacityExceeded` rather than `SlotUnavailable`, confirms twelve
      non-overlapping bookings all succeed (the lock queues, it does not
      over-refuse), and races a cancel against a create for the freed slot.
      **Every response must be 201 or 409** in all six, which is what would
      catch a 1205 escaping the retry as a 500.
- [x] All rule violations (availability, blackout, capacity) are rejected with
      clear reasons. **Met 2026-09-07** (Phase 1c): every code this endpoint can
      raise is asserted against the status its `ErrorKind` promises, alongside
      the correlation id and the message never reaching the wire.
- [x] A member can cancel their own booking; the slot is freed. **Met
      2026-09-08** (Phase 2b), asserted three ways: on an exclusive resource the
      same interval is refused before the cancel and accepted after it by a
      *different* member; the availability query goes back to one continuous
      span; and on a pooled resource one unit returns rather than the time.
- [x] The concurrency strategy is documented and defended. **Met 2026-09-07**:
      [`0023`](docs/decisions/0023-booking-concurrency-strategy.md), including
      the four rejected alternatives, the peak-vs-sum correction to §4.1, the
      RLS fail-open guard, and measured evidence from removing the lock hints.

**Open decision (source doc): "Can a TenantAdmin cancel another user's booking,
and if so, how is that user notified?"** — already answered by
[`0002`](docs/decisions/0002-tenant-admin-cancellation.md) on 2026-08-19. WP-4
implements it and raises the record with the mentor rather than re-deciding.

Notes:
- Planned in three phases (create → view/cancel → concurrency, proof and
  documentation); `dbo.CreateBooking` is written **correct from its first
  migration** rather than staged naive-then-fixed, since §4.1 leaves no
  legitimate naive path to demonstrate (owner's call, 2026-09-07).
- **Phase 2 is complete** (2026-09-08), in two chunks: 2a the two reads, 2b the
  cancel. Six shape questions were settled before 2a and are written up
  in `docs/wp4-plan.md` — the two that reach beyond WP-4 are
  **`ICurrentUser.IsInRole(Role)`**, the first time anything in
  `BookSpace.Application` can read a role (decision `0002` requires it: the same
  route serves a member and an admin, so the difference cannot be a policy on
  the action), and an **admin-only tenant-wide scope** on `GET /bookings`, which
  WP-5's approver queue will build on. Own-bookings stays the default for every
  role, a TenantAdmin included.
- **A booking read's visibility filter goes in the query, never in a comparison
  after the read.** `BookingReadRules` resolves a `BookingOwnerFilter` and the
  repository applies it as a `WHERE` clause, so a booking the caller may not see
  is never materialized and the handler's only branch is `BookingNotFound`. The
  filter is a named type rather than a `Guid?` precisely so the widened case
  cannot be reached by an omitted argument — §4.2's fail-open objection applied
  to *member* isolation rather than tenant isolation.
- **`member2@acme.test` cannot be used in an integration test.**
  `AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin` deactivates it
  permanently by design; a test using it passes alone and fails only in a full
  run, with a 401 on *login*. Use `approver@acme.test` as a second Acme account.
- **Cancelling is not a §4.1 write path, and neither is a booking read.** §4.1
  governs writes that *add* demand against `Resources.Capacity`, because only
  those can breach it; a cancellation can only reduce the units held at an
  instant. So the cancel goes through EF and one `SaveChangesAsync` — no
  procedure, no lock, no capacity check, and **no `IUnitOfWork`**, since
  `SaveChanges` is already a transaction. That contrast is what §5's
  `CreateExecutionStrategy` rule is actually about: mixing raw SQL with EF forces
  it (the create path), a pure EF unit of work does not. Concurrent cancels are
  covered by `Bookings.RowVersion` → `DbUpdateConcurrencyException` → 409.
- **`Booking.CanBeCancelled(nowUtc)` is the cancellation window**, and the test
  is on `EndsAtUtc`: a booking in progress can still be called off, one that has
  ended cannot. Load-bearing because **nothing writes
  `BookingStatus.Completed`**, so an attended meeting is still `Confirmed` and
  status alone would let a member rewrite history. A second cancellation is
  refused, deliberately unlike archiving a resource — there is an actor, a time
  and a reason to overwrite.
- **A `Pending` booking that is cancelled keeps its `ApprovalRequests` row at
  `Pending`.** Nothing withdraws it; WP-5's approve path must check the booking's
  status, or an approver could approve a cancelled booking. Flagged in
  `docs/wp4-plan.md` and `0002`'s amendment rather than fixed here.
- **§4.1's wording is wrong and this package corrects it**: the procedure must
  compare the *peak concurrent* overlapping `Quantity` against `Capacity`, not
  the **sum**, which would refuse legal bookings on any pooled resource. See
  `docs/wp4-plan.md`.
- §9's still-open DST **fall-back** item is assigned to WP-4 above and belongs to
  **WP-5**: a one-off booking is created from explicit UTC instants, so no
  ambiguous local time arises in anything WP-4 builds.

