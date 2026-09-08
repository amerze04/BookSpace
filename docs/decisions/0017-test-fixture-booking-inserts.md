# 0017 — Integration tests insert Bookings rows with raw SQL

**Status:** Decided (2026-08-28), implemented (2026-08-31), **amended 2026-09-08**
(WP-4 — the carve-out narrows now that `dbo.CreateBooking` exists; see the
amendment section)
**Requirements:** FR-3.1, FR-4.2, and WP-3's AC "the availability query
correctly excludes blackout periods and existing bookings"
**Raised by:** WP-3 planning (`docs/wp3-plan.md`, decision D4), before any WP-3
code was written. First used by WP-3 Phase 2 step 3, for
`CapacityBelowExistingBookings`.

## Context

CLAUDE.md §4.1 is the strictest rule in the repo: booking creation and approval
**must** go through `dbo.CreateBooking` / `dbo.ApproveBooking`, and nothing may
insert into `Bookings` from LINQ or `SaveChanges`. It is the only place the
zero-double-bookings guarantee (AC-1) exists, because the procedure takes
`UPDLOCK, HOLDLOCK` range locks that LINQ cannot express.

Those procedures do not exist yet — they are WP-4 work, and §11/§12 forbid
pulling a later work package's tasks forward.

But two WP-3 rules are *about* existing bookings and cannot be tested without
them:

- `CapacityBelowExistingBookings` (Phase 2) — a capacity decrease that would
  leave bookings already on the books over the new limit.
- The availability query (Phase 5) — its acceptance criterion names existing
  bookings explicitly.

So either those two go untested until WP-4, or the test fixture gets a way to
put a row in `Bookings`.

## Decision

**Integration-test fixtures may insert `Bookings` rows using raw SQL. Never
LINQ, never `SaveChanges`, and only in test fixtures.**

Raw SQL specifically, for two reasons:

1. It cannot be mistaken for a production write path. A `context.Bookings.Add`
   in a test is one copy-paste away from a handler; a hand-written `INSERT` in a
   test helper is not.
2. It adds no domain method anyone could later reuse by accident. There is no
   `Booking.CreateForTest`, no repository `Add`, nothing that shows up in
   IntelliSense next to the real API.

§4.1's guarantee is about **concurrent** production writes. A fixture inserting
one known fixed row, single-threaded, before any request runs, needs no such
guarantee — it is asserting a precondition, not exercising a write path.

**Rejected: waiting for WP-4.** It would leave `CapacityBelowExistingBookings`
with a thrower and no test through Phase 2 and Phase 5, and would make the
availability query's own acceptance criterion unverifiable in the package that
introduces it.

**Rejected: writing `dbo.CreateBooking` early just for tests.** That is
straightforwardly WP-4's task, and a procedure written to satisfy a fixture is
not the procedure AC-1 needs.

## Consequences

- `ResourceWriteEndpointTests.InsertBookingAsync` is the first and, for now,
  only such helper (WP-3 Phase 2 step 3). Phase 5 will add its own or reuse it.
- The rows it writes are ordinary and complete: `CK_Bookings_Interval`,
  `CK_Bookings_Quantity`, `CK_Bookings_Status` and
  `FK_Bookings_Resources_SameOrg` all apply, so a fixture cannot create a
  booking the schema would reject. `OrgId` is derived from the resource row
  rather than passed in, so the composite tenant FK cannot be violated.
- **The fixture connection needs an explicit RLS bypass**, which is not obvious
  and fails silently. The security policy's predicate is filter-only, so it does
  not block the `INSERT` itself — but the statement *reads* `dbo.Resources` and
  `dbo.Users` to derive `OrgId` and `UserId`, and a raw connection with no
  session context sees zero rows in both. The first version of the helper
  inserted nothing and reported no error. The same applies to the cleanup
  `DELETE`: an RLS filter predicate restricts `DELETE` too, so without the
  bypass it removes nothing and then blocks the resource teardown. Both now set
  `TenantInit` / `TenantBypass` explicitly, the same signal
  `TenantSessionContextInterceptor` sends for `TenantBypassScope` (decision
  `0013`).
- This carve-out expires when `dbo.CreateBooking` lands. It is worth
  reconsidering then whether the fixtures should switch to calling the
  procedure — probably yes for anything asserting booking behavior, and
  probably not for a fixture that just needs a row to exist.


## Amendment (2026-09-08) — the carve-out narrows, it does not expire

Made in WP-4, which is the package this record anticipated: `dbo.CreateBooking`
now exists, so the premise above — "the procedure does not exist, therefore
nothing legitimate can insert a booking" — no longer holds. The Consequences
section already asked for exactly this reconsideration, and the answer it
guessed at turned out to be the right one.

**New tests use the real path.** Every booking a WP-4 test needs as *arrangement*
is created through `POST /bookings`, and the concurrency suite races that
endpoint rather than an insert. `SeedData` goes through
`IBookingRepository.CreateAsync` too, so the seed is not an exception either.

**The existing fixtures stay**, for two reasons that the procedure's arrival does
not change:

- **They set up states the endpoint cannot produce.** A wholly-past booking is
  the clearest case: `POST /bookings` refuses one with `BookingInThePast`, and a
  booking that has already ended is precisely what
  `BookingCancelEndpointTests` has to arrange in order to assert
  `BookingNotCancellable`. Same for an in-progress booking, and for a row in a
  terminal status like `NoShow`, which nothing in the system writes at all.
- **Volume.** `AvailabilityEndpointTests` asserts the PRD's performance NFR over
  260 bookings on one resource. As 260 HTTP round trips that setup would
  dominate the test's own measurement, which is the opposite of what it is
  there to measure.

So the rule becomes: **raw SQL in a fixture is for a state the API cannot reach,
or for bulk.** Anything a caller could legitimately do, a test now does the way a
caller would. Raw SQL specifically — never LINQ, never `SaveChanges` — remains
the point, so a fixture can never be mistaken for a production path.

One thing WP-4 added to the gotcha already recorded above: the fixture's
instants must be **truncated to whole seconds**. `datetime2(0)` *rounds* on
write, so an untruncated `DateTime.UtcNow` in a fixture moved a stored boundary
by up to half a second and turned a blackout adjacency test intermittent (WP-3
Phase 4). The same trap is why `IClock.UtcNow` is truncated and why `SeedData`
truncates its clock (CLAUDE.md §4.3).

## Notes

Owner's note at the time of the decision: acceptable at this stage of
development; worth documenting but not a significant deviation.

The scope is narrow on purpose. This permits **test fixtures** to insert
bookings. It does not permit a handler, a repository, a seed routine, or a
background job to do so — for those, §4.1 stands unchanged.
