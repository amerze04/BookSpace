# WP-3 — Resources & Availability API: proposed approach

Status: drafted 2026-08-28, **before any WP-3 code was written**, and approved
by the repo owner the same day. The four decisions in "Decisions settled
before starting" below were taken as part of that approval, not left to be
discovered mid-build. They should still move into `docs/decisions/` as
numbered records (0014+) as the phases that implement them land — the same way
`0009`–`0012` did for WP-2, and `0013` for its Phase 4.

Source: `docs/Work Packages - Week 3.pdf`.

---

## What WP-3 asks for

Six tasks, one goal: "the endpoints that let an admin publish resources with
real rules, and let a member see genuine live availability."

1. **CRUD for resources** (type, capacity, timezone, description) — TenantAdmin
   only. FR-3.1, FR-3.5.
2. **Manage availability windows** per resource. FR-3.2.
3. **Manage blackout periods**; ensure they override availability. FR-3.4.
4. **Mark resources `RequiresApproval`** and assign approvers. FR-3.3.
5. **An availability query**: given a resource and a date range, return
   bookable slots.
6. **Clean DTOs, error contracts, and pagination.**

Acceptance criteria (source doc): an admin can publish a resource with
availability and blackout rules; the availability query correctly excludes
blackout periods and existing bookings; non-admins cannot create or edit
resources; the API returns clear, structured errors.

---

## The structural finding that shapes the phase order

`AvailabilityWindow` and `BlackoutPeriod` sit **outside all three tenant-
isolation mechanisms** WP-2 built. Neither implements `ITenantOwned`, neither
has an `OrgId` column, neither appears in `BookSpaceDbContext`'s global query
filters, and `Security.TenantAccessPolicy` (the `AddTenantIsolationRls`
migration) covers only `dbo.Users`, `dbo.Resources`, `dbo.Bookings`. Both are
reached purely by `ResourceId`.

This was harmless through WP-2, because nothing queried those two tables. WP-3
is the first package whose endpoints operate mostly *on* them. Written the
obvious way, a handler for `DELETE /blackouts/{id}` would be

```csharp
await _context.BlackoutPeriods.FirstOrDefaultAsync(b => b.Id == id, ct);
```

— which returns another tenant's blackout, with no filter and no RLS to stop
it. That is precisely the failure mode `CLAUDE.md` §4.2 exists to make
impossible, so it has to be closed before any WP-3 endpoint is written. See
decision D1 below; it lands in Phase 1.

---

## Proposed sequencing

Each phase is a prerequisite for the next, in the literal sense that the next
one cannot be tested without it: windows and blackouts hang off a resource,
and the availability query consumes all of them.

**Delivery style:** each phase is itself built in **small, reviewable chunks**
with control returned between them, at the owner's request — the phase is the
unit of planning, not the unit of delivery. Chunk boundaries are decided when
the phase starts and are deliberately not planned here.

### Phase 1 — API contract foundations

The WP lists "clean DTOs, error contracts, pagination" last; it is built
first. Same reasoning the mentor accepted for building the mediator before
login in WP-2: retrofitting a contract across endpoints that already exist is
how the inconsistency the AC forbids gets in.

1. **Tenant-scope the child tables (D1).** Denormalize `OrgId` onto
   `AvailabilityWindows` and `BlackoutPeriods`, implement `ITenantOwned` on
   both, add them to the global query filters and to the RLS security policy.
   New migration. Update `CLAUDE.md` §4.2's mechanism list (it currently names
   only `Users`, `Resources`, `Bookings`) once this lands. **Done 2026-08-31**
   — see decision `0014`.
2. **Pagination and DTO conventions.** A shared paged-result envelope plus
   paging/sorting query parameters — nothing paginated exists in the codebase
   yet. The WP's "clean DTOs" item lands here as the *convention* (where
   request/response types live, records not classes, domain entities never on
   the wire, how an edit payload distinguishes "not supplied" from "set to
   null", hand-written mapping); the concrete per-endpoint DTOs belong to the
   phase that owns each endpoint, since they cannot be designed before the
   endpoint is. Folded in at the owner's request on 2026-08-31, after the
   original plan left "DTOs" implicit. **Done 2026-08-31** — the owner chose
   offset paging with a total count; written up as decision `0015`.
3. **Error contracts.** Fill in the extension point deliberately left in
   `GlobalExceptionHandler.Map(...)` during WP-2, generalizing it into a
   domain-exception → reason-code mapping rather than adding a third one-off
   beside the existing `ValidationException` and `AuthenticationException`
   cases. WP-3 supplies the first real callers; §6's booking reason codes then
   slot into the same mechanism in WP-4 with no further plumbing.
   **Done 2026-08-31** — `AppException` carries an `ErrorKind`, and
   `GlobalExceptionHandler` maps kind to status once; decision `0016`.
   **Refined 2026-09-01, after Phase 2, on the mentor's advice**: each failure
   is now a named `sealed` subclass of `AppException`, which is abstract with a
   protected constructor, so a kind and a code can no longer be paired wrongly
   at a throw site — the original design could only document the pairing in a
   comment. The single mapping arm and the `ReasonCodes` catalogue are
   unchanged, and the integration suite needed no edits because the wire
   contract is identical. Written up as the amendment section of decision
   `0016`.
4. **Reason-code catalogue for WP-3**, extending `CLAUDE.md` §6's list.
   `ResourceArchived` and `BlackoutPeriod` are already there; resource-not-
   found, approver-not-in-tenant, and invalid-timezone are new.
   **Done 2026-08-31** — `ReasonCodes` holds all twelve with their kinds; the
   approver code shipped as `ApproverNotEligible` rather than
   `ApproverNotInTenant`, so it cannot confirm a cross-tenant id exists
   (reasoning in decision `0016`).

### Phase 2 — Resource CRUD (FR-3.1, FR-3.5) — **Done 2026-08-31**

The aggregate root everything else hangs off.

- **`Resource` has no edit methods today** — only `Archive`, the approver and
  availability-window collection methods, and a private `Touch`. Domain
  mutation methods are needed for name, description, type, capacity,
  timezone, durations, and the `RequiresApproval` flag.
- Read endpoints (paginated list, get-by-id) on the `TenantMember` policy;
  write endpoints (create, edit, archive) on `TenantAdmin`. Reads cannot be
  admin-only — members must browse resources in order to book them.
- No new authorization policy is required: `TenantAdmin` and `TenantMember`
  from decision `0012` already express exactly the WP-3 AC "non-admins cannot
  create or edit resources".
- Archiving already exists on the entity. There is no `Unarchive`, and the PRD
  does not ask for one; not adding it.

#### Step split (agreed with the owner on 2026-08-31, before Phase 2 started)

Four steps, at the owner's request that this phase be shorter than Phase 1's
six. Each is built and handed back for review on its own, per the delivery
style above.

1. **Domain mutators.** `Resource` gains methods for name/description/type,
   capacity, timezone, durations, and the `RequiresApproval` flag, each keeping
   the invariants that belong in the entity (capacity > 0, non-blank name) and
   touching the audit columns. Unit tests only — no EF, no HTTP, no endpoint
   yet.
2. **The read side.** `GET /resources` (paginated) and `GET /resources/{id}`:
   query + validator + handler + response DTO + controller on the
   `TenantMember` policy, with integration tests through the real pipeline.
3. **The write side.** `POST /resources` and `PUT /resources/{id}` on
   `TenantAdmin`. First throwers for `InvalidTimeZone`, `ApproversRequired` and
   `CapacityBelowExistingBookings` (decision `0016`'s catalogue).
4. **Archive plus the AC sweep.** The archive endpoint, then the cross-cutting
   assertions: a non-admin write refused, another tenant's real id returning
   404, and each reason code producing the right status over real HTTP. Roadmap
   and doc updates land here.

**Why the read side comes before the write side.** Nothing built in Phase 1 has
a caller yet — the pagination envelope, the `sort` whitelist and the error
contract are all covered by unit tests against synthetic input. Step 2 puts a
real consumer in front of all three at the earliest possible point, so an
awkward envelope or a wrong status code surfaces with one endpoint built on it
rather than four. It also means there is something worth pointing Postman at
one step sooner.

Step 3's `CapacityBelowExistingBookings` check needs `Bookings` rows to be
testable, and no booking write path exists (CLAUDE.md §4.1). It is therefore
the first real user of decision **D4**'s raw-SQL fixture carve-out; expect that
decision to be promoted to a numbered record when this step lands. **It was —
[`0017`](decisions/0017-test-fixture-booking-inserts.md).**

Manual verification in Postman begins once these four steps are done — see
"Manual verification with Postman" below.

#### What Phase 2 actually delivered, and the calls made along the way

**Two conventions changed after the phase landed, on the mentor's advice** —
both written up as amendments rather than new records, so each contract still
reads in one place: named exception subclasses per failure (amendment to
`0016`, 2026-09-01) and per-endpoint response DTOs plus `…CommandRequest`
naming (amendment to `0015`, 2026-09-01). Neither changed behaviour, with one
exception: `PUT /resources/{id}` now returns a flat body instead of wrapping the
resource in a `resource` property.

All four steps landed on 2026-08-31, one review round each. 366 unit + 142
integration tests pass, and every endpoint was additionally exercised by hand
against a running instance.

Endpoints: `GET /resources` (paged, `sort=name|resourceType|capacity`,
`includeArchived`), `GET /resources/{id}` on `TenantMember`; `POST /resources`,
`PUT /resources/{id}`, `POST /resources/{id}/archive` on `TenantAdmin`.

Decisions taken during the phase, none of which the plan had anticipated:

- **`ApproversRequired` is enforced on create as well as edit.** The "smaller
  calls" section below says "at edit time"; blocking it on create too means the
  state FR-3.3 rules out is never a resting state. Accepted consequence,
  confirmed by the owner: until Phase 3 adds approver assignment, only a
  resource that already has an approver can carry the flag.
- **Archive is `POST /resources/{id}/archive`, not `DELETE`.** §4.5 deletes
  nothing and there is no `Unarchive`, so a `DELETE` quietly meaning "archive,
  irreversibly" would mislead a client. Leaving `DELETE` unimplemented is itself
  the honest answer for a row that cannot be deleted.
- **Archive is idempotent** — a terminal state already reached is not a rule
  violation, and a retry after a dropped response has to be safe. It returns
  the resource unchanged, without moving `UpdatedAtUtc`. It is the one place
  `ResourceArchived` is deliberately *not* thrown; editing an archived resource
  still is.
- **`ResourceArchived` on `PUT`** was implemented here even though the step
  split did not list it. Step 1 had recorded that the write handlers own FR-3.5's
  edit refusal, and leaving `PUT` willing to edit archived resources would have
  been a real gap.
- **The timezone-change notice.** "The response says so explicitly" (below) is
  implemented as a nullable `TimeZoneChangeNotice` on the `PUT` response,
  carrying the previous id, the new id and the number of availability windows
  reinterpreted — a machine-readable notice rather than a prose warning, for the
  same reason a rejection carries a reason code.
- **`CK_Resources_DurationLimits`** was added (migration
  `AddResourceDurationLimitsCheck`). The schema had a `CHECK` for capacity but
  none for the duration pair, which by §6's tiering looked like a tier-1 gap;
  the owner chose consistency.
- **Two §4.3 time conventions**, both found by a failing test and a smoke check
  rather than reasoned about up front: `IClock.UtcNow` is truncated to whole
  seconds (`datetime2(0)` rounds, so a create response otherwise disagreed with
  the row it had just written), and every `DateTime` read from the database has
  `DateTimeKind.Utc` stamped back on (`datetime2` carries no offset, so JSON
  omitted the `Z` and a browser would read the value as local time).

Two things a reader might expect in the resource DTOs and will not find until
Phase 3: **availability windows** and the **assigned approver list**. Both are
Phase 3's to manage, and the approver list is worth more once there are
eligibility rules to report than as bare Guids. `requiresApproval` can
therefore read `true` with no visible approvers until then; adding either field
later is additive.

One piece of pre-existing test debt was fixed rather than worked around:
`AuthenticationEndpointTests.Login_DeactivatedUser_Returns401` permanently
deactivated the seeded `approver@acme.test` in the shared test database. That
was harmless while nothing else needed an Approver; Phase 2's "an Approver is a
non-admin too" assertions failed against it, and only in a full run. The test
now reactivates the account in a `finally`, so the suite is order-independent.

### Phase 3 — Availability windows and approvers (FR-3.2, FR-3.3) — **Done 2026-09-02**

Grouped because they are the same shape of problem: a child collection managed
*through* the aggregate root, under the Phase 1 scoping rules. Both already
have domain methods (`AddAvailabilityWindow`/`RemoveAvailabilityWindow`,
`AddApprover`/`RemoveApprover`), so this is mostly Application and API work.

#### Step split (agreed with the owner on 2026-09-01, before Phase 3 started)

**Two steps.** The owner asked for the smallest honest number; the two features
are independent — neither blocks the other — so there is no ordering to exploit,
and the only real question was whether to do both in one round. Two keeps each
review to one wire contract.

1. **Availability windows** (FR-3.2) — the domain replace method, the overlap
   rule, `PUT /resources/{id}/availability-windows`, and the schedule on the
   read detail. **Done 2026-09-01.**
2. **Approvers** (FR-3.3) plus the doc pass — the eligibility port and its
   implementation, `PUT /resources/{id}/approvers`, the approver list on the
   read detail, the `RequiresApproval` invariant enforced from the approver
   side, and the roadmap/plan updates. **Done 2026-09-02.**

#### Decisions taken before step 1, by the owner (2026-09-01)

- **Approvers use `PUT` replace-the-set too**, symmetric with windows. Per-row
  POST/DELETE would force an admin swapping approvers to empty-then-fill, which
  the `RequiresApproval` invariant would reject mid-swap.
- **Approver eligibility = holds `Approver` *or* `TenantAdmin`, in the caller's
  own tenant, and `IsActive`.** Mirrors `AuthorizationPolicies.Approver`, so
  "who may be assigned" and "who may actually approve" describe the same set —
  under the strictest reading a TenantAdmin could not be assigned even though the
  policy would let them approve. It also unblocks the practical problem that each
  tenant seeds exactly one `Role.Approver` account and no endpoint can grant the
  role. All three failures collapse to `ApproverNotEligible`, which is the point
  (decision `0016`: naming which one leaks whether the id exists at all). A
  deactivated user is refused because an inactive approver would silently stall
  approvals.
- **Windows and approvers on an archived resource are refused** (422
  `ResourceArchived`), consistent with `PUT /resources/{id}`.
- **Adjacent windows are not overlapping.** `ClosesAt` is exclusive, so
  09:00-12:00 and 12:00-17:00 coexist. Phase 5 may merge them when expanding to
  UTC intervals; that is its business, not a reason to refuse the admin's shape.

#### Availability windows (step 1)

- **Replace-the-set (PUT)**, not per-row POST/DELETE. `AvailabilityWindow`'s own
  doc comment already records why it has no audit columns: entries are
  "typically bulk-replaced as a weekly set rather than individually edited". The
  domain hinted at the API shape; following it keeps the two consistent, and
  makes the operation idempotent for free.
- **Its own endpoint, not a field on `PUT /resources/{id}`.** That payload is a
  full representation, so an admin renaming a room while omitting the windows
  array would silently wipe the schedule. The *read* detail does carry the
  schedule — asymmetric on purpose, and the one place the read response and the
  edit command deliberately diverge.
- **An empty array is legal**, meaning "opens at no time at all"; a *missing*
  array is a 400. Clearing a schedule has to be stated, never achieved by
  omission.

#### What step 1 actually delivered, and the calls made along the way

Endpoint: `PUT /resources/{id}/availability-windows` on `TenantAdmin`;
`GET /resources/{id}` now returns `availabilityWindows`, ordered by weekday then
opening time. 418 unit + 158 integration tests pass.

Three things the plan had not anticipated:

- **Enums now serialize as their names, app-wide** (`JsonStringEnumConverter`,
  `Program.cs`). `DayOfWeek` is the first enum this API ever put on the wire, and
  `{"weekday": 1}` is unreadable — 0 = Sunday is the off-by-one a client discovers
  in production. Made global rather than per-property because CLAUDE.md §5 already
  stores enums as strings and never as int, so the wire now agrees with the
  database and with the PRD's own status names; WP-4's `BookingStatus` arriving as
  `"Confirmed"` is the payoff. Safe to do globally *now* precisely because nothing
  else serialized an enum yet — a phase later it would have been a breaking change.
  Cost: the integration suite needed `Support/TestJson.cs`, since
  `System.Text.Json` reads enums from numbers only unless a client opts in.
- **`IResourceRepository.AddAvailabilityWindows`**, forced by an EF Core trap the
  integration tests found rather than one anybody reasoned about. An entity
  discovered through a collection navigation is marked **Modified**, not Added,
  when its key is already set — the same "is the key set?" heuristic
  `DbContext.Update` applies to a graph. Because `Resource` mints no ids (house
  style), every new window carried a Guid, so EF issued an `UPDATE` against a row
  that did not exist, affected zero rows, and threw
  `DbUpdateConcurrencyException`: the endpoint returned **409 `ConcurrencyConflict`
  for what was plainly an insert**, which is a spectacularly misleading symptom.
  Stating the inserts explicitly is the fix.
  Worth knowing: `Resource.AddAvailabilityWindow` has exactly the same exposure
  and hides it only because its one caller (`SeedData`) adds windows to a resource
  that is itself `Added`, so the children cascade with it. Removals need no
  equivalent — EF watches orphans leave and marks them `Deleted` correctly.
- **Sub-second times are rejected, not truncated.** `OpensAt`/`ClosesAt` are
  `time(0)`, so a fractional value would be *rounded* on write and the response
  would then disagree with the row a client reads back — the same trap CLAUDE.md
  §4.3 records for `IClock.UtcNow` and `datetime2(0)`. Rejected rather than
  clamped, matching how an oversized `pageSize` is rejected (decision `0015`):
  silently altering a submitted value is the behaviour being avoided in both.

One unrelated fix folded in at the owner's request: `POST /resources/{id}/archive`
was annotated `[ProducesResponseType<GetResourceQueryResponse>]` while returning
`ArchiveResourceCommandResponse` — leftover from before the 2026-09-01
per-endpoint DTO amendment split the two.

**Payload cap** (flagged as open on 2026-09-01, delegated to the assistant and
implemented 2026-09-02): 100 windows per request, rejected rather than truncated,
following the `pageSize` precedent. See "Settled by the owner" below.

**Also worth recording**: `CK_AvailabilityWindows_Window` requires
`ClosesAt > OpensAt`, so **a window cannot cross midnight** — a resource open
22:00-02:00 has to be two windows on consecutive weekdays. Pre-existing schema
behaviour, not introduced here, but this is the first phase where a client can hit
it. Left as-is by the owner on 2026-09-02; it becomes Phase 5's problem, together
with the one-second hole described there.

#### Approvers (step 2) — done 2026-09-02

- **Approver assignment needs validation the domain does not do.**
  `AddApprover(Guid userId, ...)` takes a bare id and checks only for
  duplicates. The Application layer confirms the user is in the same tenant,
  holds `Approver` or `TenantAdmin`, and is active — cross-tenant approver
  assignment is otherwise a leak vector. Written up as decision
  [`0018`](decisions/0018-approver-eligibility.md).
- `RequiresApproval` (the flag, a plain `Resource` property) is set in Phase 2;
  the approver list is managed here. The invariant spanning the two is now
  enforced from **both** sides — emptying the approver list on a resource that
  requires approval throws `ApproversRequired`, or the state Phase 2 blocked on
  create and edit comes back through the side door.

##### What step 2 delivered

`PUT /resources/{id}/approvers` on `TenantAdmin`, replace-the-set;
`Resource.ReplaceApprovers`; `IUserRepository` + `UserRepository`;
`ApproverNotEligibleException`;
`ResourceWriteRules.EnsureEveryApproverIsEligible`; and `approvers` on
`GET /resources/{id}`. 442 unit + 181 integration tests pass.

- **Replace-the-set here follows from the invariant, not from symmetry** with the
  windows. Swapping one approver for another through per-row POST/DELETE has to
  pass through the empty list, and an empty list on a resource that requires
  approval is exactly what `ApproversRequired` refuses. One request carrying the
  whole new set has no invalid intermediate state.
  `Replace_CanSwapApproversOnAResourceRequiringApproval` is the test that says so.
- **The Phase 2 gap is closed.** "Until Phase 3 adds approver assignment, only a
  resource that already has an approver can carry the flag" — an admin can now
  publish an approval-gated resource in two calls, assign then flag, and
  `AnAdminCanPublishAResourceThatRequiresApproval` walks that path end to end.
- **`GET /resources/{id}` stopped being a pure projection**, and this is the one
  structural change worth flagging. Approvers are an EF *owned* collection over a
  private field, reachable only through the computed `Resource.ApproverUserIds`,
  which has no SQL translation — projecting it would have meant an `EF.Property`
  expression over a backing-field name, a string the compiler does not check, in
  the middle of the query a reader most needs to follow. The detail read now
  loads the aggregate and issues a second query for the approvers' names.
  `IResourceRepository`'s argument for projecting ("select every column of every
  row to build a summary of seven") is about the *list*, where the row count is
  unbounded; it never applied to one resource by id.
- **Names on the wire, never emails**, and the read detail is on `TenantMember`
  rather than admin-only: a member deciding whether to book an approval-gated
  room should see who will be deciding, and a bare `Guid` tells them nothing. An
  address tells them more than they asked for.
- **Duplicate ids are rejected (400), not collapsed.** The domain applies set
  semantics, so a repeated id would store cleanly and the response would come
  back shorter than the request — quietly returning something other than what was
  sent, which is the behaviour decision `0015` avoids elsewhere by rejecting an
  oversized `pageSize` instead of clamping it.
- **Approvers hit none of step 1's EF trap.** `ResourceApprovers` is an owned
  collection, which EF diffs as part of its owner rather than tracking as
  independent entities, so `Clear()` + re-add saves correctly with no explicit
  `Add`. Worth recording precisely because the two collections look alike in the
  entity and behave differently in the change tracker.

##### Settled by the owner, 2026-09-02

- **Eligibility is checked at assignment time only, and that is the intended
  behaviour** — not a gap to close in WP-3. Nothing re-checks an existing
  assignment when a user is later deactivated or loses the role, so a resource can
  hold an approver who no longer qualifies. What *should* happen then (drop the
  assignment? refuse the booking? escalate to the TenantAdmin?) is a question for
  WP-4, which owns approval routing and is where the consequence actually lands —
  a request routed to an inactive approver stalls until the stale-approval expiry
  job (§7) kills it. Recorded in decision `0018`.
- **The absence of a user-listing endpoint is accepted.** An admin using only the
  API cannot discover the Guid to put in an approver list; FR-3.3 is usable from a
  UI that already has a user directory, which is the intended consumer. Not a
  WP-3 gap — user management is not in this work package.
- **Payload caps added** (delegated to the assistant's judgment, implemented the
  same day): 100 availability windows and 50 approvers per request, rejected
  rather than truncated. Neither is a guess at a real limit — both are ceilings on
  an otherwise unbounded write, since nothing else stopped one authenticated admin
  sending ten thousand rows in a single INSERT. Deliberately generous: a full
  seven-day schedule with morning and afternoon blocks is fourteen windows, so a
  request that meets the cap is a signal worth reading rather than a limit worth
  raising.
- **The midnight-crossing limitation stays as it is, and becomes Phase 5's
  problem.** `CK_AvailabilityWindows_Window` requires `ClosesAt > OpensAt`, so a
  resource open 22:00–02:00 must be two windows on consecutive weekdays. Changing
  that means a migration and a redefinition of what a window is; the cost only
  appears when Phase 5 expands windows into UTC intervals, which is where the fix
  belongs. **Sharper than first reported**: because the column is `time(0)`, the
  latest expressible `ClosesAt` is `23:59:59`, so a window running to midnight
  loses its final second and a rejoined overnight pair has a one-second hole at
  the boundary. Invisible at booking granularity, but real. Phase 5 should adopt a
  documented convention — treat `23:59:59` as end-of-day when joining
  consecutive-day windows — rather than discovering it in an off-by-one-second
  test.
### Phase 4 — Blackout periods (FR-3.4, Decision 0001)

Its own phase because it is **not** simple CRUD. Decision `0001` gives a
blackout absolute priority: it cancels every occurrence it overlaps, *any
status*, and the booking's owner is notified. So creating a blackout is a
write against `Bookings` and `Notifications`, not an insert into one table.
The WP's one-line task ("ensure they override availability") understates this
considerably.

- Placed after Phase 3 so the availability it overrides actually exists, and
  before Phase 5 because the query must exclude blackouts.
- The cancellation cascade writes `Notifications` rows for the not-yet-built
  dispatch job to pick up. That is the intended design (§7): the table exists,
  and its unique constraint is what makes the job idempotent.
- **Known testing constraint:** no booking write path exists yet, so the
  cascade is exercised against rows inserted by the D4 raw-SQL fixture rather
  than through a real booking flow.

#### Outcome — done 2026-09-02

Built in two steps. The split is worth recording because the obvious one was
rejected: "all CRUD, then the cascade" gives two evenly-sized chunks, but it
leaves a reviewable state in which an admin can create a blackout over live
bookings and nothing happens to them — a silent contradiction of decision
`0001` sitting in the tree. So the cascade was front-loaded instead:

1. **Create + read + the cascade.** `POST` and `GET`, `IBlackoutPeriodRepository`,
   `BlackoutCascade`, `Booking.CancelForBlackout`, `BlackoutPeriodElapsed`.
2. **Edit + delete.** `PUT` and `DELETE`, `BlackoutPeriod.Revise`,
   `BlackoutPeriodNotFound`, decision `0019`, and this write-up.

Step 1 is the heavier of the two, and step 2 is light. That is the accepted cost
of every chunk boundary being self-consistent.

Five questions were put to the owner before any code was written, and all five
are recorded in [`0019`](decisions/0019-blackout-period-lifecycle.md): full CRUD
rather than create-only; overlapping blackouts allowed; the cascade limited to
`Pending`/`Confirmed`; the §4.1 reading; and hard delete. The owner also asked
for an opinion on two of them, which is where the second and third rules in
`0019` came from.

**The finding that shaped the phase**, and the one worth carrying forward:
nothing in this codebase writes `BookingStatus.Completed`. There is no
`Complete()` method and no job in CLAUDE.md §7 that sets it, so a meeting that
actually happened and was checked into stays `Confirmed` indefinitely. That
turns "cancel every `Pending` or `Confirmed` booking the blackout overlaps" —
the obvious reading of `0001` — into a rule that rewrites history. Phase 4
defends against it with `Booking.CanBeCancelledForBlackout`, which requires both
a live status *and* `EndsAtUtc > now`.

It is a roadmap gap rather than a Phase 4 bug, and the fix agreed with the owner
belongs to WP-4: fold the missing transition into the existing no-show release
job, splitting past `Confirmed` bookings on `CheckedInAtUtc` (null → `NoShow`,
which it already does; non-null → `Completed`), rather than adding a fourth job.
Note the two predicates differ — `IsNoShow` measures grace from `StartsAtUtc`,
whereas "completed" is about `EndsAtUtc` having passed — so it is one job with
two conditions, not one condition with a branch.

**Four smaller things the plan did not anticipate:**

- **This feature is the first in the API to take an instant on the wire**, so it
  is the first that had to decide what a timestamp with no zone means. The
  answer is that it is refused (400): `System.Text.Json` maps a bare
  local-looking timestamp to `DateTimeKind.Unspecified`, which a handler can only
  interpret by guessing a zone, and the likeliest guess is the *server's* —
  precisely the silent app/database disagreement §4.3 exists to prevent. An
  explicit offset is accepted and normalized to UTC. WP-4's booking endpoints
  inherit the same question and should reuse `BlackoutPeriodFieldRules`' answer.
- **`Notifications` got its first writer**, which confirms §7's design end to
  end: the cascade inserts rows for a dispatch job that does not exist yet, and
  `UQ_Notifications_Once` is what makes the eventual send idempotent (AC-6).
- **`BlackoutPeriod.Reschedule` was widened into `Revise` rather than kept.** It
  took the interval only and had never acquired a production caller, and a `PUT`
  is a full representation, so it could not express the payload. Leaving it
  beside a new mutator would have created a second instance of exactly the trap
  the Phase 5 notes already record against `Resource.AddAvailabilityWindow`: dead
  code with a live-looking name.
- **The `datetime2(0)` rounding trap bit a test.** The raw-SQL booking fixture
  used an untruncated `DateTime.UtcNow` while blackout bounds were truncated to
  whole seconds; since the column *rounds*, the adjacency test intermittently saw
  a one-second overlap. The fixture now resolves offsets to whole-second instants
  once and derives the end from the start. Same family as the two §4.3
  conventions Phase 2 discovered, and the third time this project has been caught
  by a `(0)`-precision column.

**Verification:** 529 unit + 226 integration tests pass (up from 445 + 182 at the
end of Phase 3), the blackout suite re-run three times to confirm the
timing-sensitive tests are stable. No migration was needed — Phase 1's D1 work
had already built the entity, its query filter, its RLS predicate, the composite
same-org FK and `IX_BlackoutPeriods_Resource_Start`.

**Manually verified end to end by the owner in Postman on 2026-09-02**, after the
automated suite: all four endpoints, both new reason codes, the two distinct
404s, the elapsed-vs-starts-in-the-past contrast, the range filter's overlap
semantics, the archived-resource refusals on all three writes, the non-idempotent
second `DELETE`, and the non-admin 403s all behave as documented. The walkthrough
is recorded as §`06` of `docs/postman/README.md`; the committed collection does
**not** yet contain these requests, so that section is the record rather than a
runner pass.

**One thing the manual pass could not reach, by construction: the cascade.**
Every `cancelledBookings` array comes back empty from a client, because there is
no booking write path until WP-4 (§4.1). So the behaviour that makes this phase
more than CRUD is covered only by `BlackoutPeriodEndpointTests`, which inserts
booking rows by raw SQL under decision `0017` and asserts against `dbo.Bookings`
and `dbo.Notifications` directly. Worth re-verifying from a client once WP-4
gives bookings a real write path.

### Phase 5 — The availability query

**Plan approved by the repo owner on 2026-09-03**, including the five shape
questions in "Settled by the owner" below. Not yet started.

Last, because it consumes every phase above: availability windows, blackouts,
capacity, existing bookings, and the archived flag. This is WP-3's genuinely
hard problem and where D2 and D3 do their work.

#### What it serves

The query **has no FR of its own** — it is a WP-3 task item. It serves the read
side of FR-4.2 ("a booking is rejected if it falls outside availability, inside
a blackout, or exceeds capacity") and FR-6.3 ("the timezone in which a
resource's availability is expressed is defined and applied consistently"). The
PRD's member flow is the shape to build for: *"selects a resource and date →
sees live availability → picks a slot."*

It also carries the PRD's **only endpoint-specific NFR**: "availability queries
and calendar views remain responsive with realistic data volumes (hundreds of
bookings per resource)."

#### The architectural point, decided before any code

Phase 5's calculator is the **read-side twin of FR-4.2's rejection rules**,
which WP-4 implements as the tier-4 checks behind `OutsideAvailability`,
`BlackoutPeriod` and `CapacityExceeded`. Written separately, the two will
drift, and the failure mode is bad: the API offers a member a slot and then
refuses the booking for it.

So the interval algebra goes in **`BookSpace.Domain`** as pure functions over
inputs — no EF, no I/O, which is what CLAUDE.md §3 says Domain is for — and the
Phase 5 handler orchestrates it. WP-4 then reuses the same code to answer "is
this one interval bookable" instead of reimplementing it.

This is a deliberate departure from where Phases 2–4 put their rules
(`Application/Features/…Rules`). Those are validation; this is the domain's core
algorithm, and it has a second consumer arriving in the next work package.

#### The algorithm, in order

1. **Load the resource**, tenant-filtered → 404 `ResourceNotFound`, with a
   cross-tenant id indistinguishable from one that exists nowhere (AC-4).
2. **Expand windows to UTC.** For each resource-local date in range, take the
   windows matching that `Weekday` and convert `(date, OpensAt)` and
   `(date, ClosesAt)` from the resource's IANA zone to UTC. Per D3 this absorbs
   DST for free — the day is simply 23 or 25 hours long.
3. **Merge contiguous intervals.** Needed twice over: Phase 3 deliberately
   allows adjacent windows (09:00–12:00 + 12:00–17:00 coexist because `ClosesAt`
   is exclusive), and the midnight-crossing case is two windows on consecutive
   weekdays that are really one span.
4. **Subtract blackouts** — interval difference against the `BlackoutPeriods`
   overlapping the span.
5. **Subtract booked capacity.** Split what remains at every `Pending` /
   `Confirmed` booking boundary and compute `Capacity − Σ Quantity` per
   sub-interval: a sweep line over start/end events. The same arithmetic as
   `IResourceRepository.PeakConcurrentBookedQuantityAsync`, but reported per
   interval rather than reduced to a single maximum.
6. **Drop what is not bookable** — `remainingCapacity == 0`, and anything
   shorter than the resource's `MinDurationMinutes` (see Q5).

#### The DST convention, because `TimeZoneInfo`'s defaults are wrong here

`TimeZoneInfo.ConvertTimeToUtc` **throws** on a local time inside a
spring-forward gap, and for an ambiguous fall-back time it **assumes standard
time** — the *later* of the two instants. Neither is what a window wants. D3
says a range absorbs the anomaly; making that concrete needs a stated rule:

- **An invalid local time resolves to the transition instant.** A window opening
  at 02:30 inside a 02:00–03:00 gap opens at 03:00.
- **An ambiguous local time resolves to the earlier offset for a window's
  *start*, and the later offset for its *end*.** That maximises the interval and
  produces D3's 25-hour day. The default would take the later instant for both,
  quietly shortening the window by an hour.

This is the riskiest code in the phase and gets its own helper with focused
tests. It also belongs behind `ITimeZoneCatalog` rather than in a handler,
because it depends on the host's tzdata — which is the reason that abstraction
exists (see its header).

#### The port has to grow

`ITimeZoneCatalog` currently answers only `IsKnownIanaId`. Phase 5 needs real
conversion, plus the two resolution rules above.

#### Settled by the owner, 2026-09-03

Five shape questions, none of them answered by the PRD or CLAUDE.md §9:

1. **Only bookable intervals are returned** — those with
   `remainingCapacity > 0`. Not a full timeline with a kind
   (`Open`/`BlackedOut`/`Full`). The WP task says "return bookable slots" and
   the AC says the query "excludes" blackouts and bookings; a timeline is API
   surface the PRD never asks for, and can be added later without breaking this
   shape.
2. **The range is expressed as resource-local dates**, not UTC instants.
   Decision `0003` makes availability resource-local, and the PRD flow says
   "selects a resource and date". Instants would force the server to decide
   which local days they touch anyway.
3. **Maximum span 90 days**, refused as a plain `ValidationFailed` 400 rather
   than a new reason code — the same treatment an oversized `pageSize` gets
   (decision `0015`). Consequence worth noting: **Phase 5 may add no reason
   codes at all**, which would be a first for WP-3.
4. **An archived resource returns an empty interval list**, not 422
   `ResourceArchived`. FR-3.5 keeps archived resources readable, "nothing is
   bookable" is the true answer, and a 422 on a read is out of character with
   every other read in this API. The response carries `isArchived` so a client
   can tell an empty result apart from a closed schedule.
5. **Intervals shorter than the resource's `MinDurationMinutes` are dropped.**
   An endpoint promising bookable time should not return a 15-minute gap on a
   resource with a 30-minute floor.

**Note for whoever promotes D2 to `0020`:** answer 1 narrows D2's wording. D2
says "free/busy intervals carrying `remainingCapacity`"; the endpoint returns
**free intervals only**. D2's substance is intact — an interval with a number,
not a fixed grid — but the record should say "bookable intervals" and note the
narrowing rather than repeating "free/busy".

#### Step split — four

Each built and handed back for review on its own, per the delivery style above.
Steps 1 and 2 need no database at all, which makes them quick to review.

1. **Timezone conversion and window expansion.** `ITimeZoneCatalog` grows; local
   windows become merged UTC intervals across a date range, including the two
   DST rules and the midnight bridge. Pure unit tests, no endpoint.
2. **Interval algebra and the capacity sweep.** Blackout subtraction,
   `remainingCapacity`, the minimum-duration filter. Also pure.
3. **The endpoint.** Query, handler, validator, DTOs, repository port and
   implementation, controller, integration tests — bookings via decision
   `0017`'s raw-SQL fixture, plus a seeded-volume smoke test for the NFR.
4. **Cleanup, docs, AC sweep.** The dead-method cleanup below; promote D2 and D3
   to `0020` and `0021`; write up the midnight convention; the cross-cutting AC
   pass; roadmap and Postman docs.

Three steps is possible by folding 1 and 2 into one "calculator" step, but those
are the two hardest things in the phase and separating them means each is
reviewed on its own.

#### What step 1 delivered — done 2026-09-03

Timezone conversion and window expansion, no database and no endpoint. 578 unit
tests pass (49 new); the 226 integration tests were re-run unchanged, since
nothing in this step is reachable from a client yet.

New in `BookSpace.Domain/Availability/`:

- **`UtcInterval`** — a half-open `[StartUtc, EndUtc)` span, the unit every later
  step works in. It refuses a non-`Utc` `DateTimeKind` and refuses an empty span:
  emptiness is expressed by an interval being *absent* from a list, so no later
  step has to ask whether an interval it was handed is real.
- **`IntervalAlgebra.Merge`** — ordered, non-overlapping output, joining
  overlapping *and merely touching* intervals. Touching is the common case here,
  not a corner: it is what collapses Phase 3's deliberately adjacent windows and
  what rejoins the two-row overnight schedule.
- **`AvailabilityWindowExpansion.ExpandToUtc`** — the weekly schedule over a
  resource-local date range, merged.
- **`IResourceTimeZone`** — the one architectural thing the plan did not
  anticipate; see below.

New in `BookSpace.Infrastructure/Time/`: **`SystemResourceTimeZone`**, the two D3
resolution rules against real tzdata, handed out by `ITimeZoneCatalog
.GetResourceTimeZone(string)`.

**Where the conversion contract ended up, and why it is not quite what the plan
said.** The plan asked for two things that pull in opposite directions: the
interval algebra in `Domain` as pure functions, and the DST rules "behind
`ITimeZoneCatalog`" because they depend on the host's tzdata. A pure Domain
function cannot call an Application port, so the split is: **Domain declares
`IResourceTimeZone`** (a resolved zone, two methods, nothing else about it), and
**`ITimeZoneCatalog` hands out implementations of it**. The tzdata dependency is
still behind the abstraction, exactly as the plan wanted; the *interface* just
lives one project lower than the plan assumed. The alternative — Domain resolving
zone ids itself — would have put the host's timezone database inside code that is
supposed to have no I/O.

A useful side effect: the risky code and the algebra are now tested separately.
`SystemResourceTimeZoneTests` asserts the two rules against real
`America/New_York` (and `Australia/Lord_Howe`, whose gap is 30 minutes rather than
an hour, so nothing can be hard-coded to a whole hour);
`AvailabilityWindowExpansionTests` runs mostly against a fixed-offset fake zone,
so the expansion loop, the merge and the midnight convention cannot fail because
a CI image ships different tzdata. The 23-hour and 25-hour day assertions use the
real zone, since that is the one claim a fake cannot make.

**The two DST rules, verified rather than assumed.** Probed against this
machine's tzdata before any code was written, and the plan's description of
`TimeZoneInfo`'s defaults is confirmed: `ConvertTimeToUtc` throws on a local time
in a clocks-forward gap, and for an ambiguous local time it returns the *later*
instant. Under the rules as implemented, `2026-03-08` in `America/New_York`
expands to 23 hours and `2026-11-01` to 25.

Finding the transition instant for a gap needed a decision the plan did not
reach. `TimeZoneInfo` does not expose a gap's own start without unpacking
`AdjustmentRule.DaylightTransitionStart`'s floating "second Sunday in March"
form, and `GetUtcOffset` on an invalid local time returns the *pre*-transition
offset, which reproduces the "shift it forward by an hour" behaviour D3 does not
want. So `SystemResourceTimeZone` walks forward one second at a time to the first
local time the zone considers real and converts that. Exact for anything this
system can express (`time(0)` columns, and every tzdata transition falls on a
whole minute), bounded at four hours so it can never spin, and it runs only for a
local time actually inside a gap — twice a year at most per resource.

**Two things worth the owner's eye, both behaviour rather than plumbing:**

- **The midnight convention is now `AvailabilityWindowExpansion.ClosesAtEndOfDay`
  and it applies unconditionally**, not only when there is a next-day window to
  join. A `ClosesAt` of exactly `23:59:59` is read as the following midnight. The
  plan framed this as a convention "when joining consecutive-day windows", but
  making it conditional would mean the same stored window means two different
  things depending on what its neighbour happens to be. Unconditional is also the
  more faithful reading: `CK_AvailabilityWindows_Window` and `time(0)` leave an
  admin no other way to say "until midnight". Cost: one second of availability
  granted on a window that closes at end-of-day with nothing following it —
  invisible at booking granularity, and the direction an admin intended.
- **A window lying entirely inside a clocks-forward gap disappears for that date
  only.** It opens and closes at the same instant, so it is dropped rather than
  reported as a zero-length slot. That is D3 working as intended — those local
  times did not happen that day — but it is the one case where a resource's
  schedule silently produces less than it says, so it has a test of its own next
  to one proving the same window is ordinary a week later.

Not done in this step, as planned: blackout subtraction, the capacity sweep, the
minimum-duration filter (all step 2), and everything with a database (step 3).
`IntervalAlgebra` holds only `Merge` so far.

#### What step 2 delivered — done 2026-09-03

The rest of the calculation, still with no database and no endpoint. 621 unit
tests pass (43 new); the 226 integration tests were re-run unchanged.

- **`IntervalAlgebra.Subtract`** — interval difference, which is how a blackout
  overrides the weekly schedule (FR-3.4). It removes *instants*, so it can take a
  chunk out of the middle of an open span and leave two. Both sides are merged
  first, which is also what makes decision `0019`'s **overlapping blackouts** need
  no special handling — a pile of them merges into the single span it means.
- **`CapacitySweep.Subtract`** — the other subtraction, and the one that is not
  about time. A booking removes *units*, not instants (decision `0005`), so a
  1-unit booking against a capacity of 4 leaves the same time open with 3 left,
  and time only disappears once the units run out. Implemented as a sweep line:
  each booking becomes a claim event and a release event, and one ordered pass
  keeps a running total. Reports the figure per interval rather than reducing the
  range to a single worst case, which is the difference from
  `IResourceRepository.PeakConcurrentBookedQuantityAsync`.
- **`BookedQuantity`** and **`BookableInterval`** — the sweep's input and output.
  `BookableInterval` is decision **D2** as a type: an interval and a number, never
  a boolean and never a fixed grid. It refuses a remaining capacity of zero, so
  the name cannot be false.
- **`AvailabilityCalculator.BookableIntervals`** — all four steps as one call.

**One addition beyond the step's stated contents, and why.** The plan listed step
2 as the pieces and left orchestration to step 3's handler. `AvailabilityCalculator`
composes them here instead. The reason is the same one behind putting the algebra
in `Domain` at all: WP-4 has to answer "may this booking be created" over the same
four inputs, and if it composes the steps itself the two orderings will drift —
which is precisely the failure the architectural note warns about. Composing costs
about forty lines and gives WP-4 one call to make. The cost is that step 3's
handler is now thinner than the plan implies: load, call, map.

**Two ordering decisions inside the calculator, both load-bearing:**

- **Blackouts are subtracted before bookings.** A booking inside blacked-out time
  has already been cancelled by decision `0001`'s cascade, so counting its units
  against a span the blackout removed would be arithmetic on a row whose meaning
  is gone. The other order gives the same intervals here but would start to
  matter the moment a cascade missed something.
- **Spans that cannot hold the booking are dropped *before* the survivors are
  rejoined**, not after. Doing it the other way round would rejoin spans *across*
  a fully-booked gap and report time that is not free. Rejoining is confined to
  one open interval, so spans either side of a closing time or a blackout stay
  separate whatever their capacity says.
  *(As first built, on 2026-09-03, this read "zero-capacity spans are dropped
  after adjacent equal-capacity spans are rejoined" — the sweep cut at every
  capacity change and joined only neighbours with an identical figure. The
  ordering argument is unchanged; what a "wall" means widened on 2026-09-04.)*

**One consequence of Q5 worth the owner's decision, found while testing it —
and fixed on 2026-09-04.** Recorded here as it was found, because the diagnosis
is the useful part.

The minimum-duration floor was applied to the intervals as they came out of the
sweep, which is what Q5 says. But the sweep split at every capacity change, so a
1-unit booking in the middle of an open day left three intervals, and the two
flanking it could fall under the floor and disappear — even though a 1-unit
booking spanning the whole run *would* be accepted by WP-4. On a resource with a
4-hour floor, an hour-long booking at 16:00 hid the whole afternoon before it.

It looked inherent in D2's response shape — one remaining-capacity figure per
interval cannot also say "at least one unit, for longer" — and was written up as
a limitation with a test pinning it. It was not inherent. **The filter was
measuring the wrong thing**: constant-capacity fragments rather than runs a
booker could take. The root cause was that "how long can I book" has no single
answer on a pooled resource without knowing how many units the caller wants.

**Resolved by the `quantity` parameter** (owner's call, 2026-09-04; option C-i of
three offered). Given a quantity, a segment that cannot hold it is a wall,
everything between two walls is one interval carrying the floor across it, and
the filter measures those — correct by construction rather than by a second rule.
Written up in [`0020`](decisions/0020-bookable-interval-semantics.md)'s
amendment; the test that pinned the bug became
`AShortBookingNoLongerHidesTimeThatIsStillBookable`.

#### What steps 3 and 4 delivered — done 2026-09-03

The endpoint, and the phase's cleanup and documentation. 634 unit + 263
integration tests pass (13 net new unit tests — 16 added, 3 deleted with the
methods they covered — and 37 new integration tests).

**Step 3, the endpoint.** `GET /resources/{id}/availability?from=&to=` on
`TenantMember`. Three queries and no more, whatever the range length: the
resource with its schedule, the blackouts overlapping the span, the live bookings
overlapping the span. Everything after that is in memory.

- `IAvailabilityRepository` — its own port over three tables, following
  `IBlackoutPeriodRepository`'s precedent that a port should match the question.
  It differs in owning **no writes at all**, so there is no `SaveChangesAsync`.
  Its two interval methods return **Domain value types** rather than DTOs,
  because the consumer is the calculator rather than a response mapper — which
  leaves the handler nothing to convert.
- `AvailabilityWindowExpansion.LocalDateRangeToUtc` — the UTC bounds of the
  requested local dates, so the rows fetched and the windows expanded cannot be
  scoped to different spans. Added to the Domain rather than computed in the
  handler for exactly that reason.
- `AvailabilityQueryRules` — the 90-day cap and the inclusive day count. Nothing
  here throws an `AppException`, which is the point: an over-long range is a
  malformed request, so it is `ValidationFailed` 400 like an oversized
  `pageSize`. **The phase added no reason code at all.**
- The handler short-circuits an archived resource before the second and third
  queries, since neither could change the answer. Asserted, because that is the
  kind of optimisation that quietly stops holding.

**Step 4, cleanup and docs.** The three dead methods deleted (see below);
`SeedData` moved onto `ReplaceAvailabilityWindows`; D2 and D3 promoted to
[`0020`](decisions/0020-bookable-interval-semantics.md) and
[`0021`](decisions/0021-daylight-saving-for-availability-ranges.md); the midnight
convention written up as [`0022`](decisions/0022-availability-window-midnight-convention.md).

**Three calls worth flagging:**

- **The midnight convention got its own numbered record rather than a paragraph
  inside `0021`.** The plan said "write up the midnight convention" without
  saying where. It is not a DST question — it is about `time(0)` being unable to
  express `24:00:00` and `CK_AvailabilityWindows_Window` forbidding a wrap — so
  folding it into the DST record would have filed it under the wrong cause.
- **The AC-4 route table gained the availability route.** `ResourceAcceptanceTests
  .EveryRouteTakingAnId_TreatsAnotherTenantsRealIdAsNotFound` is parameterised
  over every resource route, so a new one that takes an id belongs in it or the
  claim stops being true. *Pre-existing gap, deliberately not changed:* the
  blackout routes are still absent from that table — they take a body and a
  nested id, so `SendWriteAsync` would need work — and their cross-tenant
  coverage lives in `BlackoutPeriodEndpointTests` instead.
- **Most integration tests use a resource in the `UTC` zone.** The conversion
  rules already have thorough unit tests against real tzdata, and a resource
  whose local time *is* UTC keeps these assertions about what the endpoint
  excludes rather than about arithmetic the test would have to redo to state its
  own expectation. Two tests use `America/New_York` on fixed dates for the 23-
  and 25-hour days, and one for the ordinary offset.

**One trap the fixture avoided, worth recording.** The volume test's first
assertion was `NotEmpty` plus a capacity range — which would have passed just as
happily if the raw-SQL inserts had affected zero rows, the exact failure decision
`0017` warns about. It now asserts the **exact** interval count (weekdays × 8,
because four bookings cut an eight-hour day into eight alternating spans) and the
presence of a reduced figure. Getting that count wrong on the first attempt is
how the weakness was found.

#### The inherited cleanup — now answerable

Caller counts checked 2026-09-03:

| Method | Production callers | Trap? |
|---|---|---|
| `Resource.RemoveAvailabilityWindow` | none | no |
| `Resource.RemoveApprover` | none | no |
| `Resource.AddAvailabilityWindow` | one (`SeedData`) | **yes** |
| `Resource.AddApprover` | one (`SeedData`) | no |

**Delete the first three**, converting `SeedData` to `ReplaceAvailabilityWindows`.
That kills the trap outright — a window added through `AddAvailabilityWindow` to
an already-tracked resource is marked `Modified`, saves as a zero-row UPDATE and
surfaces as a 409 `ConcurrencyConflict` for what is plainly an insert — and
leaves exactly one way to mutate the schedule, the way the API already uses.

Cost, so it is not a surprise: roughly a dozen test call sites use
`AddAvailabilityWindow` for arrangement and move to `ReplaceAvailabilityWindows`.
Mechanical, but it touches several test files.

**`AddApprover` stays.** Approvers are an EF *owned* collection, which EF diffs
as part of its owner, so it carries no equivalent trap — it is simply a method
with one legitimate caller.

**Done 2026-09-03.** 20 call sites, not a dozen. Most were arrangement and moved
to a test-only `Resource.AddWindow` extension
(`tests/BookSpace.UnitTests/ResourceScheduleArrangement.cs`) that appends *via*
`ReplaceAvailabilityWindows` — so the append semantics exist nowhere in
production, which is the whole point of deleting the method.
`AvailabilityWindowTests` deliberately does not use it: that file is *about* the
weekly schedule, so its arrangement goes through the real API too. Its four tests
of the deleted method were **rewritten** rather than deleted — what they assert
(properties kept, `OrgId` stamped from the owner, `ClosesAt > OpensAt`) belongs
to `AvailabilityWindow` and still goes through the same constructor. Three tests
in `ResourceTests` *were* deleted, being tests of the deleted methods themselves.
Four stale comments elsewhere in the codebase named the removed method and were
updated to say what replaced it.

#### Risks

- **The midnight one-second hole needs the documented convention**, not
  discovery in a test. `ClosesAt` is `time(0)`, so the latest expressible value
  is `23:59:59` and a rejoined overnight pair has a one-second gap at the
  boundary. Treat `23:59:59` as end-of-day when joining consecutive-day windows.
- **The NFR is what tests will not catch.** Three queries over a 90-day span
  plus an in-memory sweep should be fine, but "responsive with hundreds of
  bookings per resource" deserves a deliberate check rather than an assumption.
- **CLAUDE.md §9's DST fall-back question stays open.** D3 resolves it for
  *ranges*, which is all Phase 5 touches. The *occurrence* case — an instant,
  which has to land somewhere — is untouched and belongs to WP-4. Phase 5 must
  not quietly close it.

The final AC sweep lands here: publishing a resource with rules end-to-end,
the query excluding blackouts and bookings, non-admin writes rejected, and
structured errors throughout. Per-phase tests are written inside each phase;
this is the cross-cutting pass.

---

## Manual verification with Postman (from the end of Phase 2)

Agreed with the owner on 2026-08-31: once Phase 2 lands there are real
endpoints, and the owner will exercise them by hand in Postman **in addition
to** the unit and integration tests, not instead of them. Phase 1's contract
(the paged envelope, the error shapes) has no consumer until then, so a human
hitting live endpoints is the first honest test of whether it is pleasant to
consume.

The API already serves an OpenAPI document in Development
(`Program.cs` calls `AddOpenApi()`/`MapOpenApi()`), so
`GET http://localhost:5270/openapi/v1.json` can be imported straight into
Postman rather than hand-building every request. Endpoints appear in it as they
are written.

**Correction, found 2026-08-31 while verifying Phase 2 step 2:** that URL
returns **401** without a bearer token. The deny-by-default `FallbackPolicy`
(decision `0012`) covers `MapOpenApi()` too, so the document has to be fetched
with a token attached like any other request. Open for the owner: either leave
it and remember the token step, or `MapOpenApi().AllowAnonymous()` in the
Development branch only — opening an endpoint is an authorization decision, so
it was left alone.

Minor, related: the document names the list endpoint's query parameters
`Page`/`PageSize`/`Sort`/`IncludeArchived` (PascalCase, from the request
record's properties), so a Postman import generates `?Page=1`. Query binding is
case-insensitive, so both spellings work.

- URLs: `http://localhost:5270`, or `https://localhost:7079` (the dev
  certificate will need SSL verification turned off for that environment).
- Seeded accounts, all with password `Passw0rd!` (`SeedData.SeedPassword`,
  development only):

  | Email | Role | Use for |
  |---|---|---|
  | `admin@acme.test` | TenantAdmin | create / edit / archive resources |
  | `member1@acme.test` | Member | reads, and proving a non-admin write is refused |
  | `approver@acme.test` | Approver | Phase 3's approver assignment |
  | `admin@globex.test` | TenantAdmin | the second tenant, for cross-tenant checks |
  | `sysadmin@bookspace.local` | SysAdmin | *not* the resource endpoints — see below |

### Three behaviours that look like bugs and are not

1. **A SysAdmin token gets 403 on resource endpoints.** The `TenantMember`
   policy requires an `orgId` claim, and decision `0012` deliberately omits
   that claim for a SysAdmin (PRD §2: the Platform Operator must never see
   tenant booking content in routine operation). Use `admin@acme.test`.
2. **Re-sending an already-rotated refresh token kills the whole token
   family** (decision `0011`), so the next call fails with 401
   `RefreshTokenReuseDetected`. That is the feature working; in Postman, where
   re-sending an old request is one click, it reads as a random 401. Log in
   again. Access tokens last 15 minutes.
3. **403 and 422 come from different layers.** A non-admin write is refused by
   the authorization policy — a plain 403 with no reason code. A rule refusal
   comes from a handler as 422 with a `reasonCode` (decision `0016`). Both are
   correct; which one you get tells you whether you are testing RBAC or the
   domain.

### Two checks worth doing deliberately

These are the acceptance criteria a demo turns on, and both are easier to be
convinced by from a client than from a test log:

- Log in as `member1@acme.test` and request a **real** Globex resource id.
  Expect 404 with no hint the id exists anywhere (AC-4).
- Attempt `POST /resources` as `member1@acme.test`. Expect 403 — the WP-3 AC
  "non-admins cannot create or edit resources".

Setting an `X-Correlation-Id` header on requests makes the console output
readable while poking around: it comes back on the response and tags every
Serilog line for that request.

### What Postman does not cover

The automated suite stays the source of truth for the things a client cannot
prove: concurrency (AC-1), RLS enforcement at the database level with no EF
involved, and job idempotence (AC-6). Postman is for response shapes, error
contracts and demonstration — a different job, not a substitute.

**Open, for the owner to decide:** whether the collection is committed
(`docs/postman/`, plus an environment file and a README section) or kept local.
It is not part of the work package either way.

---

## Decisions settled before starting

Taken by the repo owner on 2026-08-28, in response to this plan. To be written
up as numbered records as the implementing phase lands.

### D1 — `AvailabilityWindows` and `BlackoutPeriods` get their own `OrgId`

**Implemented 2026-08-31 and promoted to
[`docs/decisions/0014-child-table-tenant-scoping.md`](decisions/0014-child-table-tenant-scoping.md)**,
which is now the authoritative record — including the migration details this
plan could not have anticipated (nullable-add-then-backfill, and switching the
RLS policy off around the backfill because the migration's own connection
cannot grant itself a bypass).

**Decided: denormalize.** Add `OrgId` to both tables, implement `ITenantOwned`,
extend the global query filters and the RLS policy to cover them.

Rejected alternative: an architectural rule that the two are only ever reached
through the (already filtered) `Resource` aggregate. That works, costs no
migration, and is what a smaller change would look like — but it is a
*convention*, and `CLAUDE.md` §4.2's entire premise is that isolation must not
depend on remembering one. Decision `0006` already set the house precedent
with `Bookings.OrgId`: duplicate the column when it buys enforceable isolation,
and back it with a composite FK so the two values cannot physically disagree.
The same composite-FK technique (`FK_..._Resources_SameOrg` against
`UQ_Resources_Org_Id`) applies here unchanged.

Owner's reasoning: a migration is still cheap at this stage, and this is a core
requirement rather than a nicety.

### D2 — A "bookable slot" is an interval with remaining capacity

**Decided: free/busy intervals carrying `remainingCapacity`**, not fixed-size
discrete slots.

Decision `0005` makes `Capacity` a count of *concurrent units*, so a slot is
not bookable/not-bookable — it has a number of units left. A boolean response
shape would contradict the capacity model. Discrete slots would additionally
bake a grid size into the API that the PRD never asks for, and force a choice
of granularity that belongs to the client.

### D3 — DST is resolved for availability *ranges* now; the instant question stays open

**Decided: expand a window to the actual elapsed UTC interval on that date**,
which handles both the spring-forward gap and the fall-back doubling with no
policy choice — the day simply has 23 or 25 hours.

This is a genuinely easier question than the one `0008` answers, and the
difference is worth recording: a *window* is a range, so it can absorb a
missing or repeated hour by just being shorter or longer. An *occurrence* is
an instant, and an instant has to land somewhere, which is why spring-forward
needed a documented skip policy and why the fall-back case is still open.

**§9's still-open fall-back question is unaffected and stays open** — resolving
it for ranges here does not resolve it for instants.

### D4 — Bookings are inserted by raw SQL in the integration fixture

**Implemented 2026-08-31 (Phase 2 step 3) and promoted to
[`docs/decisions/0017-test-fixture-booking-inserts.md`](decisions/0017-test-fixture-booking-inserts.md)**,
which is now the authoritative record — including the gotcha this plan could not
have anticipated: the fixture connection needs an explicit RLS bypass, because
the `INSERT`'s own `SELECT` over `Resources`/`Users` (and the cleanup `DELETE`)
silently affects zero rows without one.

**Decided: allow it, document it.** The AC "the availability query correctly
excludes … existing bookings" cannot be tested without booking rows, and
`dbo.CreateBooking` is WP-4 work that §11/§12 forbid pulling forward.

Raw SQL specifically, **not** LINQ or `SaveChanges`: it cannot be mistaken for
a production write path, and it adds no domain method anyone could later
reuse by accident. This is a narrow, deliberate carve-out from `CLAUDE.md`
§4.1, scoped to the integration-test fixture only. §4.1's guarantee is about
concurrent production writes; a fixture inserting one known fixed row needs no
such guarantee.

Owner's note: acceptable at this stage of development; worth documenting but
not a significant deviation.

---

## Smaller calls made without a formal decision record

Flagged in the plan proposal and not objected to. Any of these can be revisited
cheaply; none changes the schema.

- **Overlapping availability windows on the same weekday are rejected**, not
  unioned. The schema has no constraint either way.
  **Landed Phase 3 step 1** as `ReasonCodes.OverlappingAvailabilityWindow`, 409 —
  `ErrorKind.Conflict` rather than `Validation`, because every window in the
  payload is individually well-formed and it is the *set* that contradicts
  itself, which no per-field validator error can point at. **Adjacent windows are
  not overlapping**: `ClosesAt` is exclusive, so 09:00-12:00 and 12:00-17:00 are
  two legal windows (owner's call, 2026-09-01).
- **`RequiresApproval = true` with zero approvers is blocked** at edit time.
  FR-3.3 reads "can be marked `RequiresApproval`, with one or more assigned
  approvers", so the empty state is not a valid resting state.
  **Landed Phase 2 step 3, widened to create as well as edit** — see "What
  Phase 2 actually delivered" above. `ReasonCodes.ApproversRequired`, 422.
- **A capacity decrease that would put existing bookings over the new limit is
  rejected.** **Landed Phase 2 step 3** as
  `ReasonCodes.CapacityBelowExistingBookings`, 422. It compares the new capacity
  against the **peak of concurrent `Quantity`** (the maximum, over every future
  booking's start instant, of the units held at that instant) — not a sum over
  the range, which would overcount, since two bookings can both overlap a third
  without overlapping each other. `Pending`/`Confirmed` only, future only:
  reducing capacity cannot invalidate history. Advisory by design — §4.1 keeps
  the real capacity guarantee in `dbo.CreateBooking` under a range lock.
- **Changing `TimeZoneId` reinterprets all existing availability windows**, and
  the response says so explicitly rather than silently shifting them.
  **Landed Phase 2 step 3** as a nullable `TimeZoneChangeNotice` on the `PUT`
  response.

---

## Still open

- **The DST fall-back case for recurring booking occurrences** — unchanged by
  D3, which resolves the range case only. Still the single open item in
  `CLAUDE.md` §9; see the Notes section of `0008`. WP-3 does not need it.
- **Whether `Resource` needs an `Unarchive`.** Not in the PRD, not being built;
  noted so a future session does not read its absence as an oversight.

## What WP-3 deliberately does not touch

- `dbo.CreateBooking` / `dbo.ApproveBooking` and any booking write path — WP-4.
- Recurrence expansion — the availability query reads `Bookings` rows, it does
  not create or expand series.
- The notification *dispatch* job. Phase 4 writes `Notifications` rows; sending
  them is §7 work in a later package.

---

## Corrections after WP-3 closed — 2026-09-04

Not part of the work package. A review pass over the resource model, prompted by
the owner asking whether `ResourceType` should mean anything, surfaced a cluster
of related problems; three were fixed in one pass (options **A-i**, **B-i**,
**C-i** of the set offered, with the capacity coupling declined). 666 unit + 279
integration tests pass.

### What was wrong

Nine problems in three clusters, and the relationships mattered more than the
list:

**What a resource *is*.** `ResourceType` was a free `NVARCHAR(50)` with no
domain, sortable but not filterable, and nothing branched on it. Nothing
distinguished an *exclusive* resource from a *pooled* one — `Capacity = 1`
already expresses it, but that reading was written down nowhere. And the seed
data proved the gap was real: `Conference Room A` had **capacity 8**, meaning
eight simultaneous bookings of one room, which is the "seats" reading decision
`0005` exists to forbid, sitting in the dataset the project demos from.

**What the duration limits *do*.** `MaxDurationMinutes` was enforced **nowhere at
all**. `MinDurationMinutes` was read in exactly one place — the availability
query's filter — and WP-4 needs both on a booking, at which point the minimum
would have existed twice.

**What the response can *express*.** The endpoint took no quantity, so "how long
can I book" had no single answer on a pooled resource; the minimum-duration floor
therefore measured the wrong thing and hid bookable time; and because the
response has no `kind`, the loss was invisible to a client.

The clusters connect through the capacity model, not through the label:
**the availability bug cannot occur on a capacity-1 resource**, because there a
constant-capacity run *is* a free run and the floor measures exactly the right
thing. That is provable, and it is why the two topics arrived together.

### What was done

- **A-i.** `ResourceType` is an enum (`Room | Equipment | Vehicle | LabSlot |
  Other`) stored as its name with `CK_Resources_ResourceType`; `?type=` filters
  `GET /resources`; the seed's capacity corrected to 1; decision
  [`0005`](decisions/0005-capacity-semantics.md) amended with the
  exclusive/pooled reading and with why the type does **not** constrain
  capacity.
- **B-i.** `Resource.CanFitABooking(span)` and
  `Resource.AllowsBookingDuration(duration)` — one place, two named questions,
  so WP-4's rejection and the read filter cannot drift.
- **C-i.** `quantity` on the availability query, default 1;
  [`0020`](decisions/0020-bookable-interval-semantics.md) amended.

The capacity coupling ("a Room must be capacity 1") was **declined** by the
owner, on the analysis that the label and the booking model do not line up.

### Two things the pass turned up that were not in the plan

- **`JsonStringEnumConverter` reads case-insensitively**, so `"room"` is accepted
  and stored as `Room`. Deliberately left as-is and asserted
  (`Create_AcceptsATypeInAnyCasingAndStoresItCanonically`), because unlike a
  timezone id — which `ITimeZoneCatalog` keeps canonical by refusing
  `"america/new_york"` — an enum is stored as the enum's own name whatever the
  client sent, so strictness would protect nothing. Found by a test that
  *expected* a 400 and instead leaked a resource into the shared test database,
  breaking every count assertion downstream.
- **The integration suite needed `TestJson.Options` on ~50 more
  deserializations.** Putting an enum on the wire means a client has to opt in to
  read it, which is the same consequence Phase 3 recorded when `DayOfWeek` became
  the first enum in a response — the note there said so, and this pass is what it
  was predicting.

### Suggested, not done

The seed data now has **no pooled resource at all** (both templates are capacity
1), so nothing in the demo dataset exercises `remainingCapacity` below full or
the `quantity` parameter. Adding one — "Pool Cars", capacity 5 — would make the
seed teach the distinction `0005` now documents. Left out because it changes the
resource counts several tests assert on, which is a separate, mechanical change.
