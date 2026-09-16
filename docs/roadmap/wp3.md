_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-3 — Resources & Availability API — **Done** (2026-09-03)
Source doc: `docs/Work Packages - Week 3.pdf` (weeks 2–3, backend track).
Plan and settled decisions: `docs/wp3-plan.md`.

- [x] CRUD for resources (type, capacity, timezone, description) —
      TenantAdmin only. FR-3.1, FR-3.5. **Done 2026-08-31** (Phase 2):
      `GET /resources` (paginated, `sort`, `includeArchived`) and
      `GET /resources/{id}` on `TenantMember`; `POST /resources`,
      `PUT /resources/{id}` (full representation) and
      `POST /resources/{id}/archive` on `TenantAdmin`. Reads are deliberately
      **not** admin-only — a member has to browse resources in order to book
      one; the AC says non-admins cannot *create or edit*.
      Archive is `POST .../archive`, not `DELETE`: §4.5 deletes nothing and
      there is no `Unarchive`, so a `DELETE` that quietly meant "archive,
      irreversibly" would mislead. It is idempotent (a terminal state already
      reached is not a rule violation, and a retry has to be safe) — the one
      place `ResourceArchived` is deliberately not thrown; editing an archived
      resource still is.
      `RequiresApproval = true` is refused on **create** as well as edit
      (`ApproversRequired`), so the state FR-3.3 rules out is never a resting
      state. Consequence, accepted by the owner: until Phase 3 adds approver
      assignment, only a resource that already has an approver can carry the
      flag.
- [x] Manage availability windows per resource. FR-3.2. **Done 2026-09-01**
      (Phase 3 step 1): `PUT /resources/{id}/availability-windows` on
      `TenantAdmin`, replace-the-set rather than per-row POST/DELETE — the domain
      asked for that shape first, since `AvailabilityWindow` carries no audit
      columns precisely because entries are "bulk-replaced as a weekly set". It
      is therefore idempotent, and an empty array is a legal schedule meaning
      "opens at no time", distinguished from a *missing* array (400) so a
      schedule can only be cleared by asking to clear it.
      Deliberately **not** a field on `PUT /resources/{id}`: that payload is a
      full representation, so an admin renaming a room while omitting the array
      would silently wipe the schedule. The read side does carry it —
      `GET /resources/{id}` now returns `availabilityWindows`, ordered by weekday
      then opening time, projected in the same SQL query.
      First thrower for `OverlappingAvailabilityWindow` (409): overlaps on one
      weekday are rejected, not unioned, and **adjacent windows are not
      overlapping** (`ClosesAt` is exclusive, so 09:00–12:00 and 12:00–17:00
      coexist) — the owner's call, 2026-09-01.
- [x] Manage blackout periods; ensure they override availability. FR-3.4.
      **Done 2026-09-02** (Phase 4, two steps: create + read + the cascade, then
      edit + delete). Decision
      [`0019`](docs/decisions/0019-blackout-period-lifecycle.md).
      `POST /resources/{id}/blackout-periods`,
      `PUT /resources/{id}/blackout-periods/{blackoutId}` (full representation) and
      `DELETE .../{blackoutId}` on `TenantAdmin`;
      `GET /resources/{id}/blackout-periods` on `TenantMember` — paginated
      with an optional `from`/`to` **overlap** filter, and its own endpoint
      rather than a collection on `GET /resources/{id}` because blackouts
      accumulate for the life of a resource while the weekly schedule and the
      approver list are bounded sets.
      Plain create rather than Phase 3's replace-the-set: blackouts are
      individual events with their own audit columns, and decision 0001's
      cascade makes resending one anything but free. **Overlapping blackouts on
      one resource are allowed** (owner's call, 2026-09-02) — deliberately
      unlike availability windows, because the union of two blackouts is still
      blacked out, whereas two overlapping windows contradict each other.
      Creating a blackout **cancels every booking it overlaps and enqueues a
      `Notifications` row per cancellation, in the same `SaveChanges`** —
      `Booking.CancelForBlackout` plus `BlackoutCascade`. The response lists the
      cancellations (`cancelledBookings`, with `recurrenceRuleId` where the
      booking was a series occurrence), because an admin who just cancelled
      other people's meetings should be told in the reply (PRD AC-2).
      First thrower for the new `BlackoutPeriodElapsed` (422): a blackout whose
      interval is **entirely** in the past is refused, since it blocks nothing.
      The test is on `EndsAtUtc`, deliberately not `StartsAtUtc` — a blackout
      that began this morning and runs to Friday is the ordinary case.
      **The cascade cancels only bookings that have not yet ended**, not merely
      live-status ones, because nothing writes `BookingStatus.Completed` (see the
      Notes below) and status alone would let a blackout cancel attended meetings.
      `PUT` re-runs the cascade over the **new** interval — decision 0001 names
      widening, but moving matters as much — and runs **forwards only**: narrowing
      restores nothing, because a cancellation is irreversible.
      `DELETE` is a **real hard delete**, the first in this codebase, returning 204
      and un-cancelling nothing; a second call is 404 `BlackoutPeriodNotFound`
      (the second new code, 404), deliberately not 204. An archived resource
      refuses all three writes.
- [x] Mark resources `RequiresApproval` and assign approvers. FR-3.3.
      **Done 2026-09-02** (Phase 3 step 2): `PUT /resources/{id}/approvers` on
      `TenantAdmin`, replace-the-set like the schedule — but here it follows from
      the invariant rather than from symmetry, since swapping approvers per-row
      would have to pass through the empty list, which is refused. Eligibility
      is own-tenant + `IsActive` + (`Approver` or `TenantAdmin`), all three
      failures collapsing to one `ApproverNotEligible` (422) that says nothing
      about which applied: [`0018`](docs/decisions/0018-approver-eligibility.md).
      `GET /resources/{id}` now returns `approvers` (id and full name, no email),
      visible to any `TenantMember` — a member deciding whether to book an
      approval-gated room should see who will be deciding.
      **This closes the gap Phase 2 accepted**: until now only a resource that
      already had an approver could carry the flag, so an admin can finally
      publish an approval-gated resource — assign approvers, then set the flag.
      The invariant is now enforced from both sides; emptying the list on a
      resource that requires approval is `ApproversRequired` (422).
- [x] Build an availability query: given a resource and date range, return
      bookable slots. **Done 2026-09-03** (Phase 5).
      `GET /resources/{id}/availability?from=&to=` on `TenantMember` — the
      endpoint the PRD's member flow runs on ("selects a resource and date →
      sees live availability → picks a slot"). The range is **resource-local
      dates**, both required, **max 90 days**, refused as a plain
      `ValidationFailed` 400 rather than a new code — so this phase added **no
      reason code at all**, a first for WP-3.
      Returns **only bookable intervals**, each carrying `remainingCapacity`
      ([`0020`](docs/decisions/0020-bookable-interval-semantics.md)), cut
      wherever that figure changes so it holds at every instant inside the span.
      An **archived resource returns an empty list with `isArchived: true`**,
      not 422 — FR-3.5 keeps it readable and "nothing is bookable" is the true
      answer; the flag is what distinguishes that from a resource that simply
      never opens. Intervals shorter than the resource's `MinDurationMinutes`
      are dropped.
      The calculation lives in **`BookSpace.Domain/Availability/`** as pure
      functions — `AvailabilityCalculator` over `AvailabilityWindowExpansion`,
      `IntervalAlgebra` and `CapacitySweep` — not in the handler, because WP-4's
      `OutsideAvailability` / `BlackoutPeriod` / `CapacityExceeded` rejections
      ask the same question of one interval and a second implementation would
      drift from this one. The handler loads, calls and maps: **three queries,
      regardless of range length**.
      Two subtractions, and the distinction is the heart of it: a blackout
      removes **instants** (`IntervalAlgebra.Subtract`), a booking removes
      **units** (`CapacitySweep.Subtract`) — so one unit of four leaves the time
      open with three left, and time disappears only once the units run out.
- [x] Design clean DTOs, error contracts, and pagination — pagination and the
      DTO conventions landed with Phase 1 (offset paging with a total count,
      `PagedResult<T>`, the `sort` whitelist, and the DTO rules WP-2 had only
      implicitly: [`0015`](docs/decisions/0015-api-contract-and-pagination.md)).
      The error contract landed too: `AppException` + `ErrorKind` mapped to
      status codes once, and the `ReasonCodes` catalogue behind §6's list
      ([`0016`](docs/decisions/0016-error-contract-and-reason-codes.md)).
      Phase 2 added the resource DTOs against those conventions, and on
      2026-09-01 the mentor refined the conventions themselves: `IRequest<T>`
      types are named `…CommandRequest`/`…QueryRequest`, and every endpoint owns
      its own response record rather than sharing one (both written up in
      `0015`'s amendment). The resource endpoints now declare
      `ListResourcesQueryResponse`, `GetResourceQueryResponse`,
      `CreateResourceCommandResponse`, `UpdateResourceCommandResponse` +
      `TimeZoneChangeNotice` and `ArchiveResourceCommandResponse`, with the HTTP
      request records still nested in the controller. Left unchecked: the blackout
      and slot DTOs, which land with Phases 4–5. Phase 3 added its own against the
      same conventions — `ReplaceAvailabilityWindows*` with a
      `ReplacedAvailabilityWindow` item and `ReplaceApprovers*` with an
      `AssignedApprover` item, each paired with a *separate*
      `AvailabilityWindowDetail` / `ApproverDetail` on the read detail. Four pairs
      of identical records, and the clearest case yet for the per-endpoint rule:
      the read detail is the one a **member** sees, so it is the one that has to
      stay conservative, while a write echo is free to grow admin-only fields.
      One genuinely shared type appeared, `ApproverSummary` — a *port* output from
      `IUserRepository`, not an endpoint contract, on the same footing decision
      `0015` gives `IssuedTokens`.
      **Complete as of 2026-09-03.** Phase 4 added the blackout DTOs and Phase 5
      the last of them — `GetResourceAvailabilityQueryResponse` with a
      `BookableIntervalDetail` item, both per-endpoint and in their own files.
      Phase 5 needed **no new reason code**, so the error contract took the
      endpoint unchanged, which is the strongest evidence `0016`'s design works:
      an over-long range is a malformed request, not a domain refusal, so it is
      `ValidationFailed` 400 like an oversized `pageSize`. Nothing here is paged —
      the range cap bounds the result instead, which is why `PagedResult<T>` does
      not appear: the response is one resource's own answer, not a collection
      whose size the client controls.

Acceptance criteria (source doc):
- [x] An admin can publish a resource with availability and blackout rules —
      **met 2026-09-02**, when Phase 4 supplied the blackout half. An admin
      creates a resource, gives it a weekly schedule, assigns approvers and blacks
      out spans on it, and a member of the same tenant immediately sees all four
      (`ResourceAcceptanceTests`, `AvailabilityWindowEndpointTests`,
      `ApproverEndpointTests`, `BlackoutPeriodEndpointTests`).
- [x] The availability query correctly excludes blackout periods and existing
      bookings — **met 2026-09-03** (Phase 5). Proved at both levels.
      In the unit suite, over the pure calculator: blackout subtraction
      (including the union of overlapping blackouts, and one covering a whole
      day), and the capacity sweep (partial consumption, full consumption,
      concurrent bookings summing, a booking starting before the range).
      Through the real HTTP pipeline in `AvailabilityEndpointTests`, with
      blackouts created via the Phase 4 endpoint and bookings inserted by
      decision `0017`'s raw-SQL fixture — including the case a naive query gets
      wrong: **only `Pending` and `Confirmed` consume capacity**, asserted
      against all six `BookingStatus` values, and bookings on a *different*
      resource change nothing.
      Also covered there: the PRD's only endpoint-specific NFR
      ("responsive with realistic data volumes — hundreds of bookings per
      resource"), as a 90-day range over 260 bookings asserting both the exact
      interval count and a time bound.
- [x] Non-admins cannot create or edit resources — **met 2026-08-31**. Every
      write route is asserted forbidden to a Member *and* to an Approver
      (`Approver` sits between Member and TenantAdmin, so "non-admin" has to
      mean every non-admin), parameterized over the routes so a write endpoint
      added later without `[Authorize]` is a visible omission from the list.
      A SysAdmin is refused too — `TenantMember` requires the `orgId` claim
      that decision `0012` omits for them.
- [x] API returns clear, structured errors — **met for everything with a
      thrower, 2026-08-31**. One table asserts each reason code this phase can
      raise against the status its `ErrorKind` promises
      (`ResourceNotFound` 404, `InvalidTimeZone` 400, `ApproversRequired` 422,
      `ResourceArchived` 422, `OverlappingAvailabilityWindow` 409,
      `ApproverNotEligible` 422, `ValidationFailed` 400 with per-field errors),
      plus that every error body is a `ProblemDetails` carrying the correlation
      id, and that the exception message never reaches the client.
      Phase 3 added the last two: `OverlappingAvailabilityWindow` is the first
      `Conflict` kind on the table, and `ApproverNotEligible` is additionally
      asserted to be byte-identical for a cross-tenant approver and a
      nonexistent one (decision `0018`).
      Phase 4 added `BlackoutPeriodElapsed` (422) and `BlackoutPeriodNotFound`
      (404) to the same table, the latter asserted byte-identical for a
      nonexistent blackout, another tenant's real one, and one belonging to a
      different resource (AC-4).
      **Correction, 2026-09-02:** this line previously said `BlackoutPeriod`
      was Phase 4's remaining thrower. It is not — it is a *booking* rejection
      (FR-4.5), as §6 already listed it, so it joins WP-4's group and WP-4 now
      owns seven codes with no thrower rather than six. **Every reason code this
      work package can raise now has a thrower and a row on the table.**

Planned phasing — detail and reasoning in `docs/wp3-plan.md`, which was
approved by the repo owner on 2026-08-28 before any code was written:

1. **API contract foundations** — tenant-scope the child tables (D1),
   pagination + DTO conventions, error contracts, WP-3 reason codes.
   **Done 2026-08-31**, in six reviewable steps. Delivered: the D1
   child-table scoping, in three
   steps — domain (`OrgId` + `ITenantOwned` on both entities,
   `Resource.AddAvailabilityWindow` now the only creator of an
   `AvailabilityWindow`), persistence (query filters, composite same-org FKs,
   the `AddChildTableTenantScoping` migration applied to the dev database and
   its `Down` verified by an actual revert/re-apply, RLS predicates for both
   tables), and documentation (this list, §4.2, decision `0014`, the schema
   doc); and pagination + the DTO conventions (`PagedResult<T>`, `IPagedQuery`,
   `PagingDefaults`, `SortOption`, `PagedQueryRules`, `ToPagedResultAsync`,
   decision `0015`) — unit-tested but with no consumer until Phase 2's
   `GET /resources`; and the error contract (`AppException` + `ErrorKind`,
   mapped to status codes once in `GlobalExceptionHandler`, decision `0016`),
   which fills in the extension point WP-2 left there; and the reason-code
   catalogue (`ReasonCodes` + §6's list, kinds recorded per code, decision
   `0016`'s catalogue section). 277 unit + 70 integration tests pass.
   Nothing in the phase is consumed by an endpoint yet — Phase 2 is the first
   caller of all of it, which is the accepted cost of building the contract
   before the endpoints.
2. **Resource CRUD** — FR-3.1/FR-3.5. **Done 2026-08-31**, in the four steps
   `docs/wp3-plan.md` planned (domain mutators — reads — writes — archive + AC
   sweep), reads deliberately before writes so Phase 1's contract got a real
   consumer early. 366 unit + 142 integration tests pass, and every endpoint
   was additionally exercised by hand against a running instance.
   Beyond the endpoints themselves, the phase produced:
   - `CK_Resources_DurationLimits` (migration
     `AddResourceDurationLimitsCheck`, applied and its `Down` verified by an
     actual revert/re-apply) — the tier-1 floor under
     `Resource.ValidateDurationLimits`. The schema had a `CHECK` for capacity
     but none for the duration pair; the owner chose to add it for consistency.
   - Decision [`0017`](docs/decisions/0017-test-fixture-booking-inserts.md),
     promoted from D4 when step 3 needed booking rows for
     `CapacityBelowExistingBookings`.
   - The two §4.3 time conventions (`IClock` truncated to seconds; `Utc` Kind
     restored on read), both found by a test and a smoke check rather than
     reasoned about up front.
   - `ICurrentUser` (the `sub` claim) and `ITimeZoneCatalog` (IANA-only, see
     §4.3) as new Application ports.
   Manual Postman verification of the live endpoints can start now; the plan
   doc records the seeded accounts and the three behaviours that look like bugs
   and are not.
3. **Availability windows + approvers** — FR-3.2/FR-3.3. **Done 2026-09-02**, in
   the two steps agreed with the owner on 2026-09-01 (windows, then approvers).
   Delivered `PUT /resources/{id}/availability-windows` and
   `PUT /resources/{id}/approvers`, both replace-the-set and both on
   `TenantAdmin`; `Resource.ReplaceAvailabilityWindows` / `ReplaceApprovers`;
   `AvailabilityWindowRules` and `ResourceWriteRules.EnsureEveryApproverIsEligible`;
   `OverlappingAvailabilityWindowException` (409) and
   `ApproverNotEligibleException` (422); `IUserRepository` as the tenant-filtered
   counterpart to `IAuthenticationUserRepository`; and both collections on
   `GET /resources/{id}`. Decision
   [`0018`](docs/decisions/0018-approver-eligibility.md).
   445 unit + 182 integration tests pass.
   **Manually verified end to end by the owner in Postman on 2026-09-02**, after
   the automated suite: both new endpoints, the two new reason codes, the
   RequiresApproval invariant from both sides, the AC-4 cross-tenant checks, the
   non-admin 403s, and the archive interactions all behave as documented. The
   walkthrough used is in `docs/postman/README.md`; the committed collection
   (`docs/postman/BookSpace.postman_collection.json`) automates the same path but
   has **not itself been run** — the manual pass is the real verification.
   Four things it produced that the plan did not anticipate:
   - **Enums now serialize as their names app-wide** (`JsonStringEnumConverter`
     in `Program.cs`). Forced by `DayOfWeek`, the first enum this API ever put on
     the wire — `{"weekday": 1}` is unreadable and 0 = Sunday is a classic
     off-by-one. Global rather than per-property because CLAUDE.md §5 already
     stores enums as strings, and safe to make global *now* only because nothing
     else serialized an enum yet; WP-4's `BookingStatus` arriving as
     `"Confirmed"` is the payoff. The integration suite gained
     `Support/TestJson.cs` because a client has to opt in to read them.
   - **`IResourceRepository.AddAvailabilityWindows`** — an EF Core trap found by
     a failing test, not reasoned about up front. A new entity discovered through
     a collection navigation is marked **Modified**, not Added, when its key is
     already set (the same heuristic `DbContext.Update` uses on a graph), so EF
     issued an UPDATE against a row that did not exist and the endpoint returned
     409 `ConcurrencyConflict` for what was plainly an insert.
     `Resource.AddAvailabilityWindow` has the same exposure and hides it only
     because its one caller (`SeedData`) adds windows to a resource that is
     itself Added. Removals need no equivalent — EF sees orphans leave and marks
     them Deleted correctly. **Approvers are unaffected**: they are an EF *owned*
     collection, which EF diffs as part of its owner rather than tracking as
     independent entities.
   - **Sub-second times are rejected, not truncated.** `OpensAt`/`ClosesAt` are
     `time(0)`, so a fractional value would be *rounded* on write and the
     response would disagree with the row a client reads back — the same trap
     §4.3 records for `IClock` and `datetime2(0)`. Rejected rather than clamped,
     matching how an oversized `pageSize` is rejected (decision `0015`).
   - **`GET /resources/{id}` stopped being a pure projection.** Approvers are an
     owned collection over a private field, reachable only through the computed
     `Resource.ApproverUserIds`, which has no SQL translation; projecting it
     would have meant an `EF.Property` expression over a backing-field name. The
     detail read now loads the aggregate and issues a second query for the
     approvers' names. `IResourceRepository`'s argument for projecting still
     stands for the *list*, where the row count is unbounded; it never applied to
     one resource by id.
4. **Blackout periods** — FR-3.4 plus decision `0001`'s cancellation cascade.
   **Done 2026-09-02**, in the two steps agreed with the owner (create + read +
   the cascade, then edit + delete), split that way deliberately so no chunk
   boundary left a state where a blackout could be created without cancelling
   what it covers. Delivered `POST`/`GET`/`PUT`/`DELETE` on
   `/resources/{id}/blackout-periods`; `Booking.CancelForBlackout` and
   `CanBeCancelledForBlackout`; `BlackoutPeriod.Revise` (which **replaced** WP-1's
   caller-less `Reschedule` rather than sitting beside it); `BlackoutCascade` and
   `BlackoutPeriodRules`; `BlackoutPeriodElapsedException` (422) and
   `BlackoutPeriodNotFoundException` (404); and `IBlackoutPeriodRepository`, one
   port over three tables so the cascade shares a unit of work. Decision
   [`0019`](docs/decisions/0019-blackout-period-lifecycle.md).
   **No migration** — Phase 1's D1 work had already built the entity, its query
   filter, its RLS predicate, the composite same-org FK and the index.
   529 unit + 226 integration tests pass.
   **Manually verified end to end by the owner in Postman on 2026-09-02**, after
   the automated suite: all four endpoints, both new reason codes, the two
   distinct 404s, the elapsed-vs-starts-in-the-past contrast, the range filter's
   overlap semantics, the archived-resource refusals on all three writes, the
   non-idempotent second `DELETE`, and the non-admin 403s. The walkthrough is
   §`06` of `docs/postman/README.md`; the committed collection does not yet carry
   these requests. **The cascade is the one thing a client cannot reach** —
   `cancelledBookings` is always empty from Postman, because no booking write path
   exists until WP-4, so it is covered by the integration suite alone.
   Two things the phase produced that the plan did not anticipate:
   - **`Notifications` got its first writer**, and with it the confirmation that
     §7's design works as written: the cascade inserts rows the not-yet-built
     dispatch job will pick up, and `UQ_Notifications_Once` is what makes the
     eventual send idempotent (AC-6).
   - **The `datetime2(0)` rounding trap bit a test, not production.** The raw-SQL
     booking fixture used an untruncated `DateTime.UtcNow`, which the column
     *rounds*, while blackout bounds were truncated — enough to turn the
     adjacency test into an intermittent one-second overlap. The fixture now
     resolves offsets to whole-second instants once and derives the end from the
     start. Same family as the two §4.3 conventions Phase 2 found.
5. **The availability query** — consumes all of the above; final AC sweep.
   **Done 2026-09-03**, all four steps, plan approved by the owner the same day —
   full detail in `docs/wp3-plan.md`. 634 unit + 263 integration tests pass.
   **This completes WP-3**: every task item is ticked and all four acceptance
   criteria are met. In brief:
   `GET /resources/{id}/availability` on `TenantMember`, taking a **resource-local
   date range** (max **90 days**, over-range refused as a plain
   `ValidationFailed` 400 rather than a new code — so this phase may add **no
   reason codes at all**), returning **only bookable intervals** carrying
   `remainingCapacity`. An **archived resource returns an empty list**, not a 422
   — FR-3.5 keeps it readable and "nothing is bookable" is the true answer — and
   intervals shorter than the resource's `MinDurationMinutes` are **dropped**.
   Five shape questions settled by the owner 2026-09-03; **answer 1 narrows D2**,
   whose "free/busy intervals" wording becomes "bookable intervals" when it is
   promoted to `0020`.
   **The one architectural call, made before any code**: the interval algebra
   goes in `BookSpace.Domain` as pure functions, not in
   `Application/Features/…Rules` where Phases 2–4 put their rules. It is the
   read-side twin of FR-4.2's rejection checks (`OutsideAvailability`,
   `BlackoutPeriod`, `CapacityExceeded`), which **WP-4 has to implement over the
   same data** — written twice they will drift, and the failure mode is the API
   offering a slot it then refuses.
   Two things the plan pins down that `TimeZoneInfo`'s defaults get wrong for us:
   an **invalid** local time (spring-forward gap) resolves to the transition
   instant rather than throwing, and an **ambiguous** one (fall-back) resolves to
   the *earlier* offset for a window's start and the *later* for its end — the
   default takes the later for both and quietly shortens the window by an hour.
   Four steps: expansion → algebra → endpoint → cleanup/docs/AC.
   **Step 1 done 2026-09-03** — timezone conversion and window expansion, no
   database and no endpoint, 578 unit tests passing (49 new) with the 226
   integration tests re-run unchanged. `BookSpace.Domain/Availability/` now holds
   `UtcInterval` (half-open, `Utc`-Kind enforced, an empty span unrepresentable),
   `IntervalAlgebra.Merge` (joins overlapping *and touching* intervals — touching
   is what collapses Phase 3's adjacent windows and rejoins the overnight pair),
   `AvailabilityWindowExpansion.ExpandToUtc`, and `IResourceTimeZone`;
   `BookSpace.Infrastructure/Time/SystemResourceTimeZone` implements the two DST
   rules and `ITimeZoneCatalog.GetResourceTimeZone(string)` hands it out.
   Three things the plan did not anticipate, all written up in `docs/wp3-plan.md`:
   - **The conversion contract is declared in `Domain`, not `Application`.** The
     plan wanted the algebra pure *and* the DST rules behind `ITimeZoneCatalog`,
     and a pure Domain function cannot call an Application port — so Domain
     declares `IResourceTimeZone` and the catalog hands out implementations. The
     tzdata dependency stays behind the abstraction; only the interface moved a
     project down. It also splits the tests usefully: the risky rules are asserted
     against real tzdata, the algebra against a fixed-offset fake.
   - **The midnight convention applies unconditionally**, as
     `AvailabilityWindowExpansion.ClosesAtEndOfDay`: a `ClosesAt` of exactly
     `23:59:59` is read as the next midnight whether or not a next-day window
     follows. Conditional would make one stored window mean two things depending
     on its neighbour, and `time(0)` plus `CK_AvailabilityWindows_Window` leave an
     admin no other way to say "until midnight". Cost: one second granted on an
     end-of-day window with nothing after it.
   - **Finding a gap's transition instant needed its own method.**
     `TimeZoneInfo` does not expose a gap's start, and `GetUtcOffset` on an
     invalid local time returns the *pre*-transition offset, which reproduces the
     "shift it an hour later" behaviour D3 rejects. `SystemResourceTimeZone` walks
     forward one second to the first real local time instead — exact for `time(0)`
     inputs, bounded at four hours, and only ever run for a local time actually
     inside a gap.
   **Step 2 done 2026-09-03** — the rest of the calculation, still with no
   database and no endpoint, 621 unit tests passing (43 new) with the 226
   integration tests re-run unchanged. Two subtractions, and the distinction is
   the heart of it: `IntervalAlgebra.Subtract` removes the *instants* a blackout
   covers (FR-3.4), while `CapacitySweep.Subtract` removes *units* — a 1-unit
   booking against a capacity of 4 leaves the same time open with 3 left
   (decision `0005`), and time only disappears once the units run out. The sweep
   is a sweep line over claim/release events, reporting the figure per interval
   where `IResourceRepository.PeakConcurrentBookedQuantityAsync` reduces a range
   to one worst case. `BookedQuantity` and `BookableInterval` are its input and
   output; `BookableInterval` is decision D2 as a type and refuses a remaining
   capacity of zero, so its name cannot be false. Three things to know:
   - **`AvailabilityCalculator.BookableIntervals` composes all four steps**, which
     the plan had left to step 3's handler. Same reasoning as putting the algebra
     in `Domain`: WP-4 answers "may this booking be created" over the same four
     inputs, and a second orchestration would drift from this one. Step 3's
     handler is correspondingly thinner — load, call, map.
   - **Two orderings are load-bearing.** Blackouts are subtracted before bookings
     (a booking inside blacked-out time was already cancelled by decision
     `0001`'s cascade); and spans that cannot hold the booking are dropped
     **before** the surviving ones are rejoined, since the other order would
     rejoin across a fully-booked gap and report time that is not free.
   - **Q5's minimum-duration floor had a consequence, since fixed.** As
     originally built it measured constant-capacity fragments rather than
     bookable runs, so a 1-unit booking mid-day could push the spans either side
     of it under the floor and delete them from the answer — time WP-4 would have
     accepted a booking for. **Resolved 2026-09-04** by the `quantity` parameter
     (see `0020`'s amendment); the regression test is
     `AShortBookingNoLongerHidesTimeThatIsStillBookable`.
   Carries one item inherited from Phase 3 (settled 2026-09-02): an availability
   window **cannot cross midnight**, because `CK_AvailabilityWindows_Window`
   requires `ClosesAt > OpensAt`, so 22:00–02:00 is two windows on consecutive
   weekdays that this phase has to rejoin into one continuous UTC interval. The
   column is `time(0)`, so the latest expressible `ClosesAt` is `23:59:59` and the
   rejoined pair has a one-second hole at the boundary — invisible at booking
   granularity, but it needs a documented convention (treat `23:59:59` as
   end-of-day when joining) rather than an off-by-one-second surprise.
   Also carries a **deliberate cleanup, deferred here on purpose** (owner's call,
   2026-09-02): `Resource.AddAvailabilityWindow`, `RemoveAvailabilityWindow`,
   `AddApprover` and `RemoveApprover` are barely called since Phase 3 — the API
   goes exclusively through the two `Replace…` methods. That would be ordinary
   dead code except that `AddAvailabilityWindow` **carries a live trap**: a window
   added through it to an already-tracked resource is marked `Modified`, not
   `Added`, and saves as a zero-row UPDATE that surfaces as a 409
   `ConcurrencyConflict` for what is plainly an insert (see
   `IResourceRepository.AddAvailabilityWindows`). It looks fine today only because
   `SeedData` calls it on a resource that is itself `Added`, so the children
   cascade.
   **Resolved 2026-09-03**, caller counts checked: `RemoveAvailabilityWindow` and
   `RemoveApprover` have **zero** production callers, `AddAvailabilityWindow` and
   `AddApprover` have one each (`SeedData`). Phase 5 step 4 **deletes the first
   three** and moves `SeedData` onto `ReplaceAvailabilityWindows`, which kills the
   trap and leaves one way to mutate the schedule. **`AddApprover` stays** —
   approvers are an EF *owned* collection, diffed as part of the owner, so it
   carries no equivalent trap. Cost: about a dozen test call sites move to
   `ReplaceAvailabilityWindows`.
   **Done 2026-09-03** in step 4: the three methods are gone, `SeedData` builds
   its weekday schedule through `ReplaceAvailabilityWindows`, and the 20 affected
   test call sites (more than the dozen estimated) moved across. Most were
   arrangement — "give this resource a window, then test something else" — and go
   through a test-only `Resource.AddWindow` extension
   (`tests/BookSpace.UnitTests/ResourceScheduleArrangement.cs`) that appends *via*
   `ReplaceAvailabilityWindows`, so the append semantics exist nowhere in
   production. `AvailabilityWindowTests` does not use it, deliberately: that file
   is *about* the schedule, so its arrangement goes through the real API too. The
   four tests there that covered the deleted method were rewritten against
   `ReplaceAvailabilityWindows` rather than deleted — the invariants they assert
   (properties kept, `OrgId` stamped from the owner, `ClosesAt > OpensAt`) are
   `AvailabilityWindow`'s, not the deleted method's, and go through the same
   constructor. Three tests in `ResourceTests` *were* deleted, being tests of the
   deleted methods themselves.

**Four decisions were settled up front** (`docs/wp3-plan.md`), to be written
up as numbered records 0014+ as each implementing phase lands (D1 and D4 are
done — see `0014` and `0017`; D2 and D3 land with Phase 5):
- **D1** — `AvailabilityWindows` and `BlackoutPeriods` get their own `OrgId`,
  `ITenantOwned`, query filters and RLS coverage. They were outside **all
  three** §4.2 mechanisms, which made a cross-tenant read the *natural* way to
  write a child-entity handler. Follows decision `0006`'s precedent.
  **Landed 2026-08-31, promoted to [`0014`](docs/decisions/0014-child-table-tenant-scoping.md)**;
  §4.2's mechanism list and `docs/bookspace-schema-v2.sql` are updated to match.
- **D2** — a "bookable slot" is a free/busy interval carrying
  `remainingCapacity`, not a fixed grid; forced by decision `0005`'s
  concurrent-units capacity model.
- **D3** — a DST gap or doubling inside an availability *window* is absorbed by
  expanding to the actual elapsed UTC interval (the day has 23 or 25 hours).
  **This does not resolve §9's still-open fall-back question for recurring
  occurrences**, which is about an instant, not a range.
- **D4** — integration tests insert `Bookings` rows via **raw SQL** (never LINQ
  or `SaveChanges`), a narrow documented carve-out from §4.1 so the
  "excludes existing bookings" AC is testable before `dbo.CreateBooking`
  exists in WP-4.
  **Landed 2026-08-31, promoted to [`0017`](docs/decisions/0017-test-fixture-booking-inserts.md)**
  — first used by Phase 2 step 3 for `CapacityBelowExistingBookings`.

Notes:
- Delivery style: each phase is built in **small, reviewable chunks** with
  control returned between them, at the owner's request. Chunk boundaries are
  decided at the start of each phase, not planned in the doc.
- WP-3 deliberately does not touch `dbo.CreateBooking`/`dbo.ApproveBooking`,
  recurrence expansion, or the notification dispatch job. Phase 4 writes
  `Notifications` rows; sending them is later work.


#### Corrections after WP-3 closed — 2026-09-04

Not a new work package: three fixes to what WP-3 shipped, agreed with the owner
after a review pass over the resource model. 666 unit + 279 integration tests
pass. Detail in `docs/wp3-plan.md`; the reasoning lives in the decision records.

1. **`ResourceType` became an enum with a `CHECK`, and filterable.**
   `Room | Equipment | Vehicle | LabSlot | Other`, stored as its name per §5
   (migration `AddResourceTypeDomain`, `Down` verified by a real revert), plus
   `?type=` on `GET /resources`. It was the only user-facing categorical column
   in the schema with no domain — `"Room"`, `"room"` and `"Meeting Room"` were
   three distinct types — and it was sortable but not filterable, which is an odd
   shape for a browse endpoint. **It still constrains nothing**, `Capacity` in
   particular: see `0005`'s amendment for why the label and the booking model
   don't line up. The seed's `Conference Room A` went from capacity 8 to 1, which
   is the same decision's evidence rather than a tidy-up.
2. **The duration limits became two questions on `Resource`.**
   `CanFitABooking(span)` reads only the minimum — a span longer than the maximum
   is fine, because a booker takes a piece of it — and
   `AllowsBookingDuration(duration)` applies both. Before this,
   `MinDurationMinutes` was read in exactly one place and `MaxDurationMinutes`
   **nowhere at all**: stored, constrained, echoed in responses, never enforced.
   WP-4 must call `AllowsBookingDuration` rather than re-deriving it; putting it
   on the aggregate now is what stops the minimum existing in two
   implementations, the same argument that put the interval algebra in `Domain`.
3. **The availability query takes `quantity` (default 1).** Fixes the bug step 2
   had recorded as an inherent limitation — see `0020`'s amendment and item 3 of
   the Phase 5 entry above.

