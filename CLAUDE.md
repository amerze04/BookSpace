# CLAUDE.md — BookSpace

Guidance for working on this repository. Read this before making changes.

---

## 1. What this project is

BookSpace is a multi-tenant SaaS platform for booking shared organizational
resources (rooms, equipment, vehicles, lab slots). Each tenant is an isolated
workspace. Administrators publish resources with availability and governance
rules; members create one-off or recurring bookings against live availability.

Authoritative specs live in `/docs`:

- `BookSpace_PRD_v1.docx` — what it must do (FR-* / AC-* IDs are cited throughout this file)
- `Work Packages - Week 1 and 2.docx` — the WP-0/WP-1/WP-2 task breakdown and
  acceptance criteria this build follows, in order. The mentor issues new
  work packages over time; each new one lands in `/docs` alongside this one
  and gets its own subsection in §12 below.
- `bookspace-schema-v2.sql` — the database, source of truth
- `decisions/000N-*.md` — every open decision from PRD §13, plus ones raised
  during schema/ERD review, resolved and written up individually with the
  reasoning behind each. See §9 below for the index. Check here before
  assuming a question is still unresolved — most are decided now.

**The primary acceptance bar is zero double-bookings under concurrent load.**
Everything else is negotiable. That is not.

---

## 2. Stack

| Layer | Choice |
|---|---|
| Database | SQL Server |
| Data access | EF Core (code-first migrations) |
| Backend | .NET / ASP.NET Core Web API |
| Frontend | Angular |
| Background jobs | Scheduled worker (idempotent) |
| Email | Third-party transactional provider |
| Hosting | Azure preferred, containers acceptable |

---

## 3. Project layout

```
backend/
  BookSpace.slnx
  src/
    BookSpace.Domain/          entities, enums, domain rules — no EF, no I/O
    BookSpace.Application/     use cases, DTOs, validation, interfaces
    BookSpace.Infrastructure/  DbContext, configurations, migrations, email, jobs
    BookSpace.Api/             controllers, auth, DI wiring
  tests/
    BookSpace.UnitTests/
    BookSpace.IntegrationTests/    needs a real SQL Server, not in-memory
frontend/                      Angular app
docs/                          PRD, work packages, schema, decision records
```

Dependencies point inward. `Domain` references nothing. `Api` references
`Application` and `Infrastructure`. Do not reference `Infrastructure` from
`Domain` or `Application`.

---

## 4. Hard rules

These are not style preferences. Breaking one is a bug, not a refactor.

### 4.1 Booking writes go through the stored procedure

Booking creation and approval **must** call `dbo.CreateBooking` /
`dbo.ApproveBooking`. Never insert into `Bookings` from LINQ or `SaveChanges`.

The procedure takes `UPDLOCK, HOLDLOCK` range locks and compares the **peak
concurrent** overlapping `Quantity` against `Resources.Capacity`. LINQ cannot
express those hints, and SQL Server has no exclusion constraint, so this is the
only place the guarantee exists (FR-4.2, FR-7.5, AC-1).

**Peak, not sum — corrected 2026-09-07, while planning WP-4.** This paragraph
used to say "the sum of overlapping `Quantity`", which over-counts and refuses
legal bookings on any pooled resource. Capacity 2, existing bookings
`09:00–10:00` (qty 1) and `10:00–11:00` (qty 1), a request for `09:30–10:30`
(qty 1): the sum over both is 2, so `2 + 1 > 2` rejects — but the two never
coexist, one unit is free at every instant of the request, and the booking is
legal. `CapacitySweep` and `PeakConcurrentBookedQuantityAsync` already compute
the peak, so the availability query would have offered that slot and the
procedure would then have refused it — exactly the drift
`AvailabilityCalculator`'s header exists to prevent. The guarantee is unchanged;
only the arithmetic under the lock is. See `docs/wp4-plan.md`.

`IX_Bookings_Resource_Start` is load-bearing — it is what keeps the range lock
narrow instead of table-wide. Never drop it. If you change the overlap
predicate, re-check the execution plan seeks rather than scans.

Approval re-runs the same capacity check. A slot may have been taken since the
request was made (FR-7.5, AC-5).

### 4.2 Tenant isolation is structural, not a WHERE clause

FR-1.2 requires isolation that cannot be bypassed by forgetting a filter.
Three mechanisms, all required:

- Global query filters on `Users`, `Resources`, `Bookings`,
  `AvailabilityWindows`, `BlackoutPeriods`, `RecurrenceRules` in
  `OnModelCreating` — `RecurrenceRules` joined the other five in WP-5 Phase 2
  (decision `0025`), found the same way `AvailabilityWindows`/`BlackoutPeriods`
  were in WP-3 (`0014`): reachable by resource is not automatically reachable
  *only* through its resource, once something loads it by its own id.
- `OrgId` set in `SaveChangesAsync` for added `ITenantOwned` entities
- SQL Server row-level security via a connection interceptor calling
  `sp_set_session_context`, with `Security.TenantAccessPolicy` covering the
  same six tables

Use `FirstOrDefaultAsync`, not `DbSet.Find()` — `Find` returns tracked entities
without querying and bypasses query filters. `IgnoreQueryFilters()` is allowed
only in explicitly named SysAdmin repository methods.

### 4.3 Time is UTC in the database, always

All instants are `datetime2(0)` holding UTC, with the `Utc` suffix on the
column name. `TIMESTAMP` in SQL Server is a rowversion, not a time type —
never use it for time.

Recurrence expands in the **application layer**, in local wall-clock time,
using the rule's IANA `TimeZoneId`, then converts to UTC. Do not split timezone
logic across the app and the database; `AT TIME ZONE` takes Windows zone IDs
and will not match what .NET produces (FR-6.1, FR-6.2, FR-6.3).

A stored `TimeZoneId` is an **IANA** id, and only an IANA id. On Windows
`TimeZoneInfo` resolves Windows ids too, so "can we find it" is not a
sufficient check — `ITimeZoneCatalog` also requires
`TryConvertIanaIdToWindowsId` to succeed, which is true only for a canonical
IANA id. Rejecting `"Eastern Standard Time"` and `"america/new_york"` is the
point, not an accident.

Two conventions keep app-time and stored-time from disagreeing. Both were added
in WP-3 Phase 2; break either and the mismatch is silent.

- **`IClock.UtcNow` is truncated to whole seconds.** `datetime2(0)` *rounds* on
  write, so an entity stamped at `12:00:32.9` is stored as `:33` and a create
  response built from that entity does not match the row a client reads back.
- **Every `DateTime` read from the database gets `DateTimeKind.Utc` stamped
  back on**, via a value converter applied to every `DateTime`/`DateTime?`
  property in `OnModelCreating`. `datetime2` carries no offset, so EF
  materializes `Unspecified` and `System.Text.Json` then omits the trailing
  `Z` — which a browser client parses as *local* time.

### 4.4 Secrets

Passwords and refresh tokens are hashed, never stored or logged in plaintext
(FR-2.3). Connection strings, API keys, and provider credentials come from
configuration/environment — never committed. Logs carry a correlation ID, never
personal records or secrets.

### 4.5 Nothing is deleted

Users and resources are deactivated (`IsActive`, `IsArchived`), not deleted
(FR-3.5). Foreign keys into `Bookings` are `NoAction` deliberately —
`Users` reaches `Bookings` through both `UserId` and `CancelledByUserId`, so
cascade would be a multiple-cascade-paths error anyway.

---

## 5. Database and EF Core conventions

- Migrations are code-first. Never edit an applied migration — add a new one.
- Stored procedures, RLS policies, and filtered indexes are created via
  `migrationBuilder.Sql(...)` with a matching `Down`. EF will not scaffold them.
- Enums map with `.HasConversion<string>()` plus a `CHECK` constraint. Never
  store them as `int`.
- Every FK is `NoAction` unless the cascade is single-path and obviously safe
  (`UserRoles`, `RefreshTokens`, `AvailabilityWindows`, `BlackoutPeriods`,
  `Notifications`).
- `EnableRetryOnFailure` is on, with 1205 (deadlock victim) added. That means
  **you cannot call `BeginTransaction` directly** — wrap the unit of work in
  `Database.CreateExecutionStrategy().ExecuteAsync(...)`.
- Raw SQL uses `ExecuteSqlAsync` / `FromSql` with interpolated strings
  (parameterised). Never `ExecuteSqlRaw` with string concatenation.
- Optimistic concurrency on `Bookings` via `RowVersion`. Catch
  `DbUpdateConcurrencyException` and surface it, don't swallow it.
  `Resources` gained its own `RowVersion` in the 2026-09-15 hardening pass —
  see that section below and decision `0023`'s amendment — for the same
  reason: FR-3.3's `RequiresApproval ⇒ approvers exist` invariant is checked
  and written across two endpoints, and a bare read-check-save has no way to
  notice the other one moved the ground out from under it.

Status values match the PRD exactly: `Pending`, `Confirmed`, `Rejected`,
`Cancelled`, `Completed`, `NoShow`. Note **Rejected**, not Declined.

---

## 6. Validation tiers

Put each rule in the right tier. Most bugs here come from putting a "must
never" in tier 4.

| Tier | Enforced by | Contains |
|---|---|---|
| 1 | Database constraints | Interval sanity (a booking's, and a resource's duration bounds), status domains, uniqueness, composite tenant FK |
| 2 | Locking protocol | No overbooking beyond capacity, the blackout re-check inside `dbo.CreateBooking`, approval re-check |
| 3 | RLS + query filters | Tenant isolation |
| 4 | Application code | Availability windows, blackouts, a booking's length against the resource's duration limits, approval routing |

**Blackouts are deliberately in two tiers**, and the duplication is the design
rather than drift (WP-4 Phase 1b, `0023`). The handler checks them so a client
gets a specific reason, and `dbo.CreateBooking` re-checks them **under the same
lock as capacity** so the rule cannot be lost to a race: between the handler's
check and the insert, an admin's cascade (`0001`) can select the bookings to
cancel and miss this one, because it does not exist yet. Decision `0001` gives a
blackout absolute priority — a live booking inside one must never exist — and
this table's own rule of thumb puts a "must never" in tiers 1–3. Availability
windows deliberately do **not** follow: narrowing a window cancels nothing, so
"inside a window" is not an invariant this system maintains after creation.

Rule of thumb: PRD wording of "must never" belongs in tier 1–3. "Should"
belongs in tier 4.

Rejections return a machine-readable reason code, not just a message
(FR-4.5). The catalogue is `ReasonCodes`
(`BookSpace.Application/Common/Errors/`) — add a code there and here, never
at a throw site as a literal. Authentication's five codes stay in
`AuthenticationFailureReason` on purpose; see
`docs/decisions/0016-error-contract-and-reason-codes.md`.

Bookings (declared by FR-4.5, first thrown in WP-4): `SlotUnavailable`,
`CapacityExceeded`, `OutsideAvailability`, `BlackoutPeriod`,
`ResourceArchived`, plus WP-4's own `BookingNotFound`,
`BookingNotCancellable`, `BookingDurationOutOfRange` and `BookingInThePast`.

`SlotUnavailable` and `CapacityExceeded` are both `Conflict` and are split by
what is left, not by the resource: **nothing free at any instant inside the
requested interval** is `SlotUnavailable`, **something free throughout but less
than was asked for** is `CapacityExceeded`. An exclusive resource can therefore
only ever produce the first, since `Capacity = 1` admits no quantity but 1
(owner's call, 2026-09-07).

`ApprovalRequired` was on this list and was **deleted in WP-4 Phase 1a**. FR-7.1
makes a booking on an approval-gated resource enter `Pending` rather than be
refused, so nothing will ever throw it, and `0016`'s premise is that the
catalogue describes what the API can actually return.

Resources and availability (WP-3): `ResourceNotFound`, `InvalidTimeZone`,
`CapacityBelowExistingBookings`, `OverlappingAvailabilityWindow`,
`ApproversRequired`, `ApproverNotEligible`, `BlackoutPeriodElapsed`,
`BlackoutPeriodNotFound`.

Note that `BlackoutPeriod` above is a **booking** rejection despite its name —
the interval a client asked for is covered by a blackout — so WP-3's blackout
endpoints do not throw it. Creating a blackout is refused by
`BlackoutPeriodElapsed`, and the availability query excludes blackout time
rather than raising anything.

A code travels with the `ErrorKind` recorded beside it in `ReasonCodes`, so
the same failure never arrives as a 404 from one handler and a 422 from
another. **Each failure is a named `sealed` subclass of `AppException`** that
fixes its own kind and code — `throw new ResourceArchivedException(id)`, never
a kind and a code passed as arguments. `AppException` is abstract with a
protected constructor, so the compiler enforces that; a mispaired kind is
caught by `AppExceptionCatalogueTests`. The message is for the log only.

`GlobalExceptionHandler` still has **one arm for the whole hierarchy** and maps
kind → status once, so a new failure needs a subclass and a catalogue entry,
never a case in that switch. A code with no subclass yet cannot be thrown at
all, which is why the phase that adds a thrower adds the class.
`AuthenticationException` is the one sanctioned multi-code subclass (FR-2.1
needs every credential failure to look identical). Reasoning in the amendment
section of `docs/decisions/0016-error-contract-and-reason-codes.md`.

---

## 7. Background jobs

Three jobs, all idempotent (FR-9.4, AC-6). There is deliberately no fourth
job for recurrence materialization — series are fully created up front, not
topped up over time (`decisions/0007-recurrence-materialization-horizon.md`).

- Reminder dispatch — `Notifications` where `SendAtUtc <= now` and
  `SentAtUtc IS NULL`. Also sends `RecurrenceOccurrenceSkipped`
  (`decisions/0008-dst-spring-forward-policy.md`) — the one kind not
  anchored to a `BookingId`; compose its content by joining through
  `RecurrenceRuleId`/`OccurrenceDate` instead of `BookingId`.
- No-show release — `Bookings` confirmed, past grace, `CheckedInAtUtc IS NULL`
- Stale approval expiry — `ApprovalRequests` where `Decision = 'Pending'` and `ExpiresAtUtc <= now`

Idempotence is enforced by the unique constraint on
`Notifications (BookingId, RecurrenceRuleId, OccurrenceDate, RecipientUserId,
Kind)` — a second run fails the insert rather than sending twice. Workers
claim rows with `UPDLOCK, READPAST` so parallel workers skip locked rows
instead of duplicating them.

---

## 8. Testing

Critical paths must have tests before the milestone is considered done:

- **Concurrency (AC-1)** — parallel booking attempts on one slot; assert exactly
  one succeeds. This is the single most important test in the codebase.
- **Isolation (AC-4)** — tenant A supplying tenant B's identifiers gets nothing.
- **DST (AC-3)** — recurring 02:30 across spring-forward resolves per the
  documented policy, no crash, no duplicate.
- **Approval re-check (AC-5)** — approving into a taken slot fails safely.
- **Idempotence (AC-6)** — reminder job run twice sends once.

Concurrency and isolation tests need a real SQL Server (integration tests),
not the in-memory provider — the in-memory provider has no locking and no RLS,
so it will pass tests that production would fail.

---

## 9. Decisions log

PRD §13 left several decisions to the team; schema/ERD review surfaced a few
more. **All are resolved as of 2026-08-26** (0001–0008 on 2026-08-19;
0009–0012 with WP-2 Phase 3) and written up individually in
`docs/decisions/` — read the linked doc before touching the affected
feature, the reasoning matters as much as the answer. This list is the
index; when a new decision doc is added, add its one-liner here too.

1. [`0001`](docs/decisions/0001-blackout-vs-recurring-series.md) — A blackout has
   absolute priority over a recurring series: it cancels every occurrence it
   overlaps (any status), and the booking's owner is notified.
2. [`0002`](docs/decisions/0002-tenant-admin-cancellation.md) — A TenantAdmin can
   cancel any booking in their tenant, not just their own. Every
   `Notifications` row, regardless of `Kind`, is delivered by email — no
   other channel exists.
   **Amended 2026-09-08** with the four mechanics implementation forced (WP-4
   Phase 2b): the admin's reach is the **same owner filter the reads use**, so
   "may I see it" and "may I cancel it" cannot disagree and an unreachable
   booking is an absent row rather than a comparison — hence one 404, never a
   403; the cancellation window is **`EndsAtUtc`**, so a meeting in progress is
   still cancellable and an ended one is not (`0019`'s rule reapplied, and
   load-bearing because nothing writes `Completed`); a **second cancellation is
   refused**, unlike archiving, because there is an actor and a reason to
   overwrite; and **self-cancellation enqueues no notification**, keyed off
   actor-vs-owner rather than role. It also required `ICurrentUser.IsInRole` —
   the first role read in `BookSpace.Application`.
3. [`0003`](docs/decisions/0003-availability-timezone.md) — Availability is
   expressed in the resource's timezone, not the booker's.
4. [`0004`](docs/decisions/0004-no-show-definition.md) — No-show = `Confirmed`,
   never checked in, past `StartsAtUtc + Organizations.NoShowGraceMinutes`.
   Determined by the no-show release job (system-initiated, not a person —
   `Bookings.UpdatedByUserId` stays null on this transition).
5. [`0005`](docs/decisions/0005-capacity-semantics.md) — `Capacity` means
   concurrent units, not seats within one exclusive booking.
   `Bookings.Quantity` sums against it.
   **Amended 2026-09-04** with the reading the original only ruled out:
   **`Capacity = 1` *is* exclusivity** (one room, one printer), and
   `Capacity = N` is a pool of N interchangeable units (pool cars, loaner
   laptops). Prompted by the owner asking whether `ResourceType` should force a
   room to capacity 1 — **it does not, and deliberately**: the label and the
   booking model don't line up (a pool of identical huddle rooms is a
   legitimately pooled `Room`), FR-3.1 gives the type no behaviour, and a wrong
   capacity is a data-entry error the system cannot detect. The amendment also
   records the **evidence that this needed writing down**: the seed data had
   `Conference Room A` at capacity 8 — eight simultaneous bookings of one room —
   which is exactly the seats reading `0005` forbids, corrected to 1 the same
   day.
6. [`0006`](docs/decisions/0006-orgid-denormalization.md) — `Bookings.OrgId`
   duplicating `Resources.OrgId` is deliberate (query performance +
   enforceable isolation), not accidental drift — and it's not just
   convention: `FK_Bookings_Resources_SameOrg` makes the two values
   physically unable to disagree.
7. [`0007`](docs/decisions/0007-recurrence-materialization-horizon.md) — A
   recurring series is fully materialized at creation (best-effort per
   occurrence, not atomic), capped at two calendar years past its own
   `StartDate`. No background top-up job.
8. [`0008`](docs/decisions/0008-dst-spring-forward-policy.md) — An occurrence
   whose local time falls in a DST spring-forward gap is skipped, not
   shifted. The user is told immediately (in the series-creation response)
   and again by email 14 days before the date, via the existing Reminder
   dispatch job — no new job. `Notifications` gained a second anchor
   (`RecurrenceRuleId` + `OccurrenceDate`) for this one case, since there's
   no `Booking` to reference.

9. [`0009`](docs/decisions/0009-jwt-claims-and-token-lifetimes.md) — Access
   tokens are HMAC-SHA256 JWTs carrying `sub`, `email`, `orgId`, and one `role`
   claim per role. `orgId` is **omitted entirely** for a SysAdmin rather than
   emitted empty, so no code can mistake it for a real tenant. 15-minute access
   token, 14-day **absolute** refresh window. Signing key from configuration
   only, validated at startup.
10. [`0010`](docs/decisions/0010-global-email-uniqueness.md) — An email
   identifies exactly one user platform-wide (`UQ_Users_Email`, unfiltered),
   so login takes email + password with no tenant discriminator. Replaced the
   WP-1 `(OrgId, Email)` filtered index, which allowed the same email in two
   tenants and left SysAdmin rows with no uniqueness at all. **Decided by the
   repo owner.**
11. [`0011`](docs/decisions/0011-refresh-token-hashing-and-rotation.md) —
   Refresh tokens are 256-bit CSPRNG values stored as SHA-256 (deterministic,
   because lookup is *by* hash; a salted KDF would break the index and protect
   nothing at that entropy). Rotation keeps the `FamilyId` and inherits the
   original expiry. **Reuse of a revoked token kills the whole family**;
   expiry kills only that token.
12. [`0012`](docs/decisions/0012-rbac-enforcement-model.md) — RBAC via four
   named policies plus a deny-by-default `FallbackPolicy`, so a new endpoint is
   protected unless it opts out. `TenantMember` deliberately **excludes**
   SysAdmin (PRD §2: the Platform Operator must never see tenant booking
   content in routine operation).
13. [`0013`](docs/decisions/0013-tenant-isolation-mechanism.md) — Structural
   tenant isolation is validation, not assignment: `SaveChanges*` throws if an
   `ITenantOwned` entity's `OrgId` doesn't match the current tenant, since the
   property has no setter to "stamp." RLS gets its own explicit bypass signal
   (`TenantBypassScope` + a `TenantInit`/`TenantBypass` session-context pair)
   rather than treating an unset session as "allow all."
14. [`0014`](docs/decisions/0014-child-table-tenant-scoping.md) — `AvailabilityWindows`
   and `BlackoutPeriods` carry their own `OrgId` and fall inside all three
   §4.2 mechanisms, instead of being reached by `ResourceId` alone. Composite
   FKs against `UQ_Resources_Org_Id` make the denormalized value unable to
   disagree with its resource's — decision `0006`'s technique, reapplied.
   `AvailabilityWindow`'s constructor is `internal`, so `Resource` is its only
   creator; `BlackoutPeriod`'s stays public because it sits outside that
   aggregate. **Promoted from WP-3's D1** when Phase 1 landed.
15. [`0015`](docs/decisions/0015-api-contract-and-pagination.md) — API contract
   conventions: **offset pagination with a total count** (`page`/`pageSize`/
   `sort` in, `PagedResult<T>` out), page size default 20 and maximum 100
   **rejected rather than clamped**, `sort=field` / `-field` against a
   per-endpoint whitelist, and every paged query ordered by a unique column —
   `ToPagedResultAsync` throws on an unordered query, since offset paging over
   one silently returns undefined pages. Also writes down the DTO rules WP-2
   followed implicitly: request records in the controller, commands/queries and
   response DTOs in the feature folder, sealed records, no domain entity on the
   wire, hand-written mapping, full-representation `PUT` for edits. Keyset
   paging was rejected — revisit only if an endpoint pages over `Bookings`.
   **Amended 2026-09-01 on the mentor's advice** (see the record's amendment
   section): every `IRequest<T>` implementation is named `…CommandRequest` /
   `…QueryRequest`, with its handler and validator following suit; and **response
   DTOs are per-endpoint, in their own files, never shared** — even when the
   fields are currently identical, because Phase 3's availability windows belong
   on the read detail and would otherwise appear in the create and archive
   responses too. `PagedResult<T>` stays shared (it is the envelope, not a
   response), and `IssuedTokens` — formerly `AuthenticationResult` — is
   `TokenIssuer`'s output rather than any endpoint's contract. One wire change:
   `PUT /resources/{id}` is now flat instead of wrapping the resource in a
   `resource` property.
16. [`0016`](docs/decisions/0016-error-contract-and-reason-codes.md) — one error
   contract: a handler rejects a request by throwing an `AppException`, and
   `GlobalExceptionHandler` maps `ErrorKind` —
   Validation/Unauthorized/NotFound/Conflict/RuleViolation — onto
   400/401/404/409/**422** once. A new failure needs a code and a kind, not a
   new switch case, so WP-4's booking rejections need no further plumbing.
   The exception **message never reaches the response** (log only); `Title` is
   generic per kind and the reason code carries the meaning.
   `AuthenticationException` is now an `AppException`, unchanged from outside.
   Also holds the reason-code catalogue (`ReasonCodes`), §6's list in code, with
   each code's kind beside it; authentication's five stay in
   `AuthenticationFailureReason`, and a test proves every code is unique across
   both files and matches its own member name.
   **Amended 2026-09-01 on the mentor's advice** (see the record's amendment
   section): `AppException` is now **abstract with a protected constructor**, and
   each failure is a named `sealed` subclass fixing its own kind and code, so the
   two can no longer be paired wrongly — the original design could only document
   the pairing in a comment. The single mapping arm, the catalogue, the log-only
   message and `AuthenticationException` are all unchanged, and the 142
   integration tests needed no edits, since the wire contract is identical.
17. [`0017`](docs/decisions/0017-test-fixture-booking-inserts.md) — integration
   **test fixtures** may insert `Bookings` rows with **raw SQL** — never LINQ,
   never `SaveChanges`, and only in fixtures. A narrow, documented carve-out
   from §4.1, because `CapacityBelowExistingBookings` (WP-3 Phase 2) and the
   availability query's "excludes existing bookings" AC (Phase 5) cannot be
   tested before `dbo.CreateBooking` exists in WP-4. Raw SQL specifically so it
   cannot be mistaken for a production path and adds no reusable domain method.
   **Gotcha recorded there**: the fixture connection needs an explicit RLS
   bypass, or the `INSERT`'s own `SELECT` (and the cleanup `DELETE`) silently
   affects zero rows. **Promoted from WP-3's D4** when Phase 2 step 3 landed.
   **Amended 2026-09-08** (WP-4): now that the procedure exists the carve-out
   **narrows rather than expires** — new tests and `SeedData` use the real path,
   and raw SQL in a fixture is for a state the API *cannot* reach (a wholly-past
   or `NoShow` booking) or for bulk (260 rows where 260 HTTP calls would
   dominate the measurement). Second gotcha added there: a fixture's instants
   must be truncated to whole seconds, or `datetime2(0)`'s rounding moves a
   stored boundary and turns an adjacency test intermittent.
18. [`0018`](docs/decisions/0018-approver-eligibility.md) — a resource approver
   must be **in the caller's own tenant, active, and hold `Approver` or
   `TenantAdmin`** — the same set `AuthorizationPolicies.Approver` admits, so
   "may be assigned" and "may approve" cannot disagree. All three failures
   return one `ApproverNotEligible`, saying nothing about which applied, because
   naming the tenant case would confirm a cross-tenant id exists (AC-4).
   Approvers are managed by replace-the-set `PUT`, which follows from the
   invariant rather than from symmetry: per-row endpoints would have to pass
   through the empty list, and an empty list on a resource requiring approval is
   refused. **Settled by the repo owner 2026-09-01**, implemented in WP-3 Phase 3
   step 2.
19. [`0019`](docs/decisions/0019-blackout-period-lifecycle.md) — blackout periods
   get **full CRUD** (FR-3.4 only asked for "define"); **overlapping blackouts are
   allowed**, deliberately unlike availability windows, because the union of two
   blackouts is still blacked out and nothing contradicts; `DELETE` is a **real
   hard delete**, the first in this system (§4.5 is about users and resources, and
   a blackout carries no history — the cancellation reason is a *text snapshot* on
   the booking, not a foreign key), it **un-cancels nothing**, and it is
   deliberately **not idempotent** (a second `DELETE` is 404, because 204 could not
   be returned without also accepting a cross-tenant id); the edit's cascade runs
   **forwards only**. A blackout **entirely** in the past is refused
   (`BlackoutPeriodElapsed`, 422); one that merely *starts* in the past is not —
   "the room flooded this morning" is the ordinary case. The cascade cancels
   `Pending`/`Confirmed` **and only bookings that have not yet ended**, which is
   necessary rather than tidy: **nothing in this system writes
   `BookingStatus.Completed`**, so an attended meeting is still `Confirmed`, and
   status alone would let a blackout rewrite history. **Settled by the repo owner
   2026-09-02**, implemented in WP-3 Phase 4.
20. [`0020`](docs/decisions/0020-bookable-interval-semantics.md) — a bookable slot
   is a **free interval carrying `remainingCapacity`**, not a boolean free/busy
   timeline and not a fixed grid — forced by `0005`'s concurrent-units model,
   since a slot with one of four units taken is genuinely still open. The
   number is a floor a client can trust at every instant inside the interval, and
   `BookableInterval` refuses a remaining capacity of zero so its name cannot be
   false. **WP-3's D2**, promoted when Phase 5 implemented it; its "free/busy
   intervals" wording is **narrowed to bookable intervals only** (owner's call,
   2026-09-03) — there is no `kind` discriminator, so a *gap* in the response
   means "nothing bookable here" without saying whether that is a blackout, a
   wall or a closing time.
   **Amended 2026-09-04**: the answer is now **per quantity**. The endpoint takes
   an optional `quantity` (default 1), a segment that cannot hold that many units
   is a **wall**, and everything between two walls is one interval carrying the
   **floor** across it. That replaced cutting at every capacity change, which was
   the root of the limitation this record used to close with: the
   minimum-duration floor measured constant-capacity *fragments* rather than
   bookable runs, so one 1-unit booking mid-day could delete the spans either
   side of it from the answer — time WP-4 would have accepted. "How long can I
   book" simply has no single answer on a pooled resource, and asking for the
   quantity is what makes it well-defined. Cost, accepted: a run no longer shows
   that part of it had *more* units free; a caller who needs that asks again with
   a higher quantity. Nothing changes for an exclusive resource, where the
   parameter can only be 1.
21. [`0021`](docs/decisions/0021-daylight-saving-for-availability-ranges.md) — a
   DST gap or doubling inside an availability *window* is **absorbed by expanding
   to the UTC interval that actually elapsed**: the local day is simply 23 or 25
   hours long. Concretely, two rules `TimeZoneInfo`'s defaults get wrong — a
   **missing** local time resolves to the **transition instant** (the default
   throws), and an **ambiguous** one resolves to the **earlier** instant for a
   window's start and the **later** for its end (the default takes the later for
   both, quietly shortening the window by an hour). **WP-3's D3**, promoted when
   Phase 5 implemented it. **Does not close §9's still-open fall-back question**,
   which is about an *instant* rather than a range — see below.
22. [`0022`](docs/decisions/0022-availability-window-midnight-convention.md) — a
   `ClosesAt` of exactly **`23:59:59` means the following midnight**, and
   **unconditionally**, not only when a next-day window exists to join.
   `CK_AvailabilityWindows_Window` forbids a window crossing midnight and
   `time(0)` cannot express `24:00:00`, so an overnight resource is two rows and
   `23:59:59` is the only way an admin can say "until midnight"; promoting it is
   also what turns the overnight rejoin into an ordinary merge rather than a
   special case. Conditional promotion was rejected because it would make one
   stored row mean two different things depending on its neighbour. Cost: one
   second granted on an end-of-day window with nothing after it. `23:59:00` is a
   minute short and is taken literally. **Decided 2026-09-03** during Phase 5
   step 1.
23. [`0023`](docs/decisions/0023-booking-concurrency-strategy.md) — the booking
    concurrency strategy (WP-4's hard problem, FR-4.2, AC-1):
    `dbo.CreateBooking` takes **`UPDLOCK, HOLDLOCK` key-range locks** on the
    overlapping rows and compares the **peak concurrent** quantity — not the
    sum — against capacity. `HOLDLOCK` locks the gaps, so a row that does not
    exist yet cannot appear in a range already counted; `UPDLOCK` makes the
    locks U-mode so contenders *block* rather than deadlock on their inserts;
    `IX_Bookings_Resource_Start` keeps the range to one resource; 1205 retry is
    part of the design, because the blackout re-check inverts lock order against
    decision `0001`'s cascade. Two things it records beyond the choice: §4.1's
    "sum" was **wrong** and would have refused legal bookings on a pooled
    resource, and the RLS filter policy creates a **fail-open** hole (no session
    context → zero overlapping rows → overbooking) that the procedure closes by
    reading `Resources` first. `sp_getapplock` was the main rejected
    alternative — deadlock-free, but it moves the guarantee off the data into a
    string nothing forces a caller to take. Measured: without the hints, 20
    simultaneous requests for one slot produced **3** bookings and a pool of 4
    was filled **10** times. **Decided 2026-09-07**, implemented in WP-4
    Phase 1b.
    **Evidence extended 2026-09-08** (Phase 3) with the HTTP-level figures —
    without the hints, **ten** confirmed bookings on a room that holds one — and
    with the measured deadlock data, which is the more interesting half:
    **1205 fires routinely** at ten-way contention (+0, +5, +1, +10 across four
    runs of six tests, no blackout write involved), every one absorbed by the
    retry, visible only as wall clock. That is the record's point 4 measured
    rather than argued, and why its "usually blocks" wording is the honest one.
    **Evidence extended again 2026-09-09** (WP-5 Phase 4) for
    `dbo.ApproveBooking`'s own distinct race — two decisions on one existing
    row, not two inserts into a range: without the lock, two simultaneous
    approvals of the same booking both reported Approved, and seven of ten
    did. Every one of four runs of the correct procedure deadlocked at least
    once (+8, +5, +5, +5 across fourteen tests each), more consistently than
    `dbo.CreateBooking`'s own measurement, and every deadlock was absorbed by
    the retry.

**All four of WP-3's up-front decisions are now numbered records**: D1 →
[`0014`](docs/decisions/0014-child-table-tenant-scoping.md) (Phase 1),
D4 → [`0017`](docs/decisions/0017-test-fixture-booking-inserts.md) (Phase 2),
D2 → [`0020`](docs/decisions/0020-bookable-interval-semantics.md) and
D3 → [`0021`](docs/decisions/0021-daylight-saving-for-availability-ranges.md)
(both Phase 5). Nothing in `docs/wp3-plan.md` is still awaiting promotion.

24. [`0024`](docs/decisions/0024-dst-fallback-recurrence-policy.md) — the DST
    **fall-back** case for a recurring occurrence (clocks go back, a local
    wall-clock time is ambiguous rather than nonexistent) resolves to the
    **earlier** of its two candidate UTC instants, for **both** the
    occurrence's start and its end — not `0021`'s start-earlier/end-later split,
    which is a property of a *range* allowed to stretch to 25 hours, whereas an
    occurrence's nominal duration should not silently grow by an hour. No
    occurrence is skipped (unlike spring-forward, `0008`): both instants are
    real, so there is always something to create the `Booking` from, and no new
    `Notifications` anchor is needed. Mechanically this is
    `IResourceTimeZone.ToUtcEarliest`, already built in WP-3 Phase 5 — the gap
    was the policy, not the code. **Settled by the repo owner 2026-09-08**, the
    first decision of WP-5, before any of its code was written.

**§9's "Still open" list is now empty.** The DST fall-back case above was its
last item, reassigned from WP-4 to WP-5 on 2026-09-07 and settled the next day:
WP-4 creates one-off bookings from explicit UTC instants supplied by the
client, so no ambiguous local time ever arose in anything it built; recurrence
— where a rule expands a *wall-clock* time and has to resolve the one that
occurs twice — is where the question finally had to be answered, and `0024` is
that answer. The `ResourceType` question raised on 2026-09-03 was settled
separately on 2026-09-04 and is recorded in `0005`'s amendment.

25. [`0025`](docs/decisions/0025-recurrence-rule-tenant-scoping.md) —
    `RecurrenceRules` gets its own `OrgId`, joining the global query filters,
    RLS predicate and a composite same-org FK against `Resources` — the same
    fix `0014` gave `AvailabilityWindows`/`BlackoutPeriods` in WP-3, applied
    here because it was missing entirely. Not an open question: a bug found
    while building WP-5 Phase 2's cancel endpoint, the first thing that ever
    loaded a `RecurrenceRule` by a caller-supplied id rather than only
    creating one scoped by its resource. Without the fix, a `TenantAdmin`'s
    dropped owner filter (decision `0002`'s reach) would have had no tenant
    restriction under it at all. **Found and fixed 2026-09-09**, confirmed
    with the owner before implementation.
26. [`0026`](docs/decisions/0026-notifications-series-anchor.md) — widens
    `CK_Notifications_HasContext` so `RecurrenceRuleId` alone (no
    `OccurrenceDate`) is a valid anchor, for WP-5 Phase 2's whole-series-cancel
    notification (`NotificationKind.SeriesCancelled`) — one summary row for
    the series, not tied to any single occurrence's date. A strict widening of
    decision `0008`'s original constraint; every row that satisfied it before
    still does. **Decided and implemented 2026-09-09.**

If a task needs a decision that isn't listed above and isn't in this log,
**stop and ask** rather than picking silently — same rule as always, this
log just isn't the source of new gaps anymore, it's the record of resolved
ones.

---

## 10. How to report back

At the end of every task — every time control returns to me — produce a
summary in this exact shape. Concise, but complete: every file touched gets a
line. No exceptions, no "and some minor edits".

```markdown
## Summary
One or two sentences: what was accomplished and whether it is working.

## Files
- `backend/src/BookSpace.Domain/Entities/Booking.cs` — created — booking entity with status enum and rowversion.
  The aggregate every write path in the system ends at, and the only place a
  status transition is expressed as code rather than as a string.
- `backend/src/BookSpace.Infrastructure/Persistence/BookSpaceDbContext.cs` — modified — added Bookings DbSet, global query filter.
  The single unit of work, and one of the three §4.2 isolation mechanisms: the
  filter here is what makes a forgotten `WHERE OrgId` harmless.
- `backend/src/BookSpace.Api/Controllers/BookingsController.cs` — modified — POST endpoint now returns 409 on SlotUnavailable.
  The HTTP surface for booking creation; it only binds and dispatches, so the
  rejection reasons stay in the handler.
- `backend/tests/BookSpace.UnitTests/BookingTests.cs` — modified — covers the new transition
- `docs/decisions/0020-something.md` — created — the decision behind it

## Migrations
- `20260819_AddBookingTables` — creates Bookings, ApprovalRequests; adds IX_Bookings_Resource_Start

## Verification
- `dotnet build` — passed
- `dotnet test` — 24 passed, 0 failed
- (or: not run, and why)

## Notes
- Anything incomplete, assumed, or deferred.
- Any open decision this touched.
```

Rules for the report:

- **Every file touched appears in the list**, with created / modified / deleted
  and a short phrase on what it does or what changed.
- **A file in `Domain`, `Application`, `Infrastructure` or `Api` gets one or two
  further sentences saying what the point of the file is** — the job it does in
  the system and why it exists, not a restatement of its name. The short phrase
  says what changed; these sentences say why the file is there at all, so I can
  defend it without reopening it.
  Test files, docs, migrations and frontend files are **listed only** — the
  created/modified phrase is enough for those.
- If more than ~15 files, group them by project (`Domain`, `Application`,
  `Infrastructure`, `Api`, `frontend`) but still list each one.
- Do not paste code back into the summary. I can read the files.
- Do not claim something builds or passes unless you actually ran it. If you
  did not run it, say so under Verification.
- Flag anything left half-done in Notes rather than letting it look finished.
- If you deviated from this file's rules for a reason, say which rule and why.

---

## 11. Working style

- Never run `git commit` (or `git push`). Leave changes staged/unstaged in the
  working tree — I review everything and commit it myself.
- Ask before inventing a requirement. The PRD is detailed; if something is not
  in it and not in §9 above, ask rather than assume.
- Prefer the smallest change that satisfies the requirement. This is an
  internship project — clarity beats cleverness, and I need to be able to
  defend every table and every constraint.
- When a design choice has a real trade-off, tell me both sides and which you
  picked. I will be asked to justify these.
- Cite the FR/AC identifier when implementing a requirement, in the commit
  message or a code comment where it is non-obvious.
- If you notice the schema or PRD is wrong or ambiguous, say so. Finding the
  gap is worth more than routing around it.
- Follow the work packages in `docs/Work Packages - Week 1 and 2.docx` in
  order. Don't start a later package's tasks (auth, mediator, EF configs)
  before the current one's acceptance criteria are met.

---

## 12. Build roadmap

This section **mirrors the work packages exactly** — one subsection per WP,
in the order the mentor issues them, tasks copied from the source doc as a
checklist. It is not an independently-invented build order; if a task isn't
in the current WP's list, that's a gap to flag (§11), not something to add
here on judgment. Update it whenever a task completes, a WP finishes, or a
new WP arrives from the mentor — append the new WP as its own subsection,
same format, don't renumber or rewrite the ones before it.

Don't start a later WP's tasks before the current one's acceptance criteria
are met, even if it looks convenient.

Source doc for WP-0/1/2: `docs/Work Packages - Week 1 and 2.docx`.

### WP-0 — Project Setup & Foundations — **Done** (2026-08-20)
- [x] Create private Git repo; main + feature-branch workflow — **skipped
      by owner's choice**, see Notes.
- [x] README describing the project and how to run it locally.
- [x] Scaffold solution structure: backend (`.NET`) + frontend (Angular).
- [x] Set up SQL Server locally and confirm connectivity.
- [x] `.gitignore`, `.editorconfig`, basic solution conventions.
- [x] `AI-USAGE.md` with headers to fill in throughout.

Acceptance criteria:
- [x] Repo clones and both projects build from a clean checkout following
      only the README — verified: `dotnet build`/`dotnet test` and
      `ng build` both pass.
- [ ] Branch protection / no-direct-to-main convention — documented as a
      convention in the README, but there's no repo yet to enforce it in.
- [x] Database connection confirmed from the backend — `GET /health/db`
      opens a real `SqlConnection` to the local `BookSpace` database.

Notes: git/remote setup was explicitly deferred to the repo owner rather
than done by the assistant — not a gap in scope, a deliberate choice.

### WP-1 — Data Model & Database — **Done** (2026-08-21)
- [x] Model core entities: Tenant, User, Resource, AvailabilityWindow,
      BlackoutPeriod, Booking, RecurrenceRule, ApprovalRequest.
- [x] Define relationships, keys, integrity constraints.
- [x] Decide how tenancy is represented on every ownable entity.
- [x] Plan indexing for availability lookups and overlap checks.
- [x] Produce an ERD; write initial migrations + seed data.

Acceptance criteria:
- [x] ERD exists, presented before any application code — presented to the
      mentor as one of the first steps; the full schema was built on it.
- [x] Migrations run cleanly and seed a realistic multi-tenant dataset —
      `InitialCreate` applied to a real SQL Server instance; seed produces 2
      orgs, 9 users, 4 resources, 20 availability windows, 2 blackout periods,
      2 recurrence rules; re-running is a no-op (idempotent).
- [x] Model represents a two-year weekly recurring booking without redesign —
      cap raised from one year to two (Decision #7 addendum); seed data
      includes a recurrence rule running exactly to the new boundary.
- [x] Every ownable entity is unambiguously tied to a tenant.

Notes:
- The design side of WP-1 (`docs/bookspace-schema-v2.sql`, decisions 0001–0008)
  was already done before this build order started, ERD included; this entry
  tracked turning it into EF Core code, which is now complete.
- ERD: confirmed settled — presented to the mentor early, and
  `docs/bookspace-schema-v2.sql` was built directly on it. `docs/Amer-ERD-
  Feedback.docx` / `-Response.docx` are the review that followed. No separate
  ERD image/file lives in this repo; the schema doc is its record.
- Seed data stopped short of `Bookings`, `ApprovalRequests`, `Notifications`,
  and `RefreshTokens` — the first three because §4.1 requires Booking writes to
  go through `dbo.CreateBooking`/`dbo.ApproveBooking`, which didn't exist yet;
  the last because refresh tokens are issued at login, not meaningful as static
  data. **Updated 2026-09-08 (WP-4 Phase 3)**: the seed now writes three
  bookings per tenant *through the procedure* — two `Confirmed` on Conference
  Room A and one `Pending` on the 3D Printer with its `ApprovalRequest` row —
  anchored to the next weekday at 10:00/11:00/14:00 **local** so the dataset
  never becomes historical, and skipping the seeded Christmas blackout, which
  the procedure would otherwise refuse. `Notifications` and `RefreshTokens`
  are still deliberately empty: nothing dispatches notifications yet (§7), so
  seeded rows would be permanently unsent mail.
- `ResourceApprovers`' forced EF owned-collection cascade (vs. the schema's
  `NoAction`) is a known, accepted, documented deviation — see the comment in
  `ResourceConfiguration.cs`.
### WP-2 — Backend Skeleton, Auth & Tenancy — **In progress**
- [ ] Layered architecture: API / application / domain / infrastructure — the
      four projects and their inward-pointing references have existed since
      WP-0, and as of the mediator task `BookSpace.Application` finally holds
      real application code rather than being an empty shell. Left unchecked
      deliberately: worth a look with the mentor over whether this item means
      "the structure exists" (true now) or "every layer carries its intended
      responsibilities" (not until auth and the write paths land).
- [x] Wire EF Core to the WP-1 schema — `EnableRetryOnFailure` added (5
      retries, 1205 deadlock victim included) in
      `BookSpace.Infrastructure/DependencyInjection.cs`. No explicit
      `BeginTransaction` calls exist anywhere yet, so nothing needed
      rewriting onto `CreateExecutionStrategy().ExecuteAsync(...)` — future
      transactional code (e.g. refresh-token rotation) must use it, per
      CLAUDE.md §5.
- [x] Credential login issuing access token + rotating refresh token —
      `POST /auth/login` dispatches `LoginCommand` through the mediator to
      `LoginCommandHandler` (`Application/Features/Authentication/Login/`),
      which verifies the password against its PBKDF2 hash, checks `IsActive`
      and the owning org's status, then mints a 15-minute JWT plus a refresh
      token in a **new** `FamilyId` via the shared `TokenIssuer`. Claim shape
      and lifetimes fixed by `docs/decisions/0009`. Every credential failure —
      unknown email, wrong password, deactivated user, suspended org — returns
      the same `InvalidCredentials` reason code so login isn't an
      account-enumeration oracle; the real cause goes to the log with the
      correlation ID. `Jwt:SigningKey` is never committed and `JwtOptions` is
      `ValidateOnStart()`, so a missing or under-32-byte key fails the boot.
      **Verified manually (2026-08-26)** against a real running instance: login
      with a seeded member returns a decodable JWT with the exact claim set
      from `0009` (`orgId` present for a tenant user); wrong password and an
      unknown email return byte-identical 401 `InvalidCredentials` bodies
      (aside from the per-request `correlationId`/`traceId`); a malformed
      request returns 400 `ValidationFailed` with field errors, never reaching
      the handler.
- [x] Refresh-token rotation and reuse detection — `RefreshTokenCommandHandler`
      implements the five-case table in its own header comment: unknown hash →
      401; active → rotate (revoke old, set `ReplacedByTokenId`, new token in
      the **same** family inheriting the **original expiry** so the window stays
      absolute); expired → 401 revoking that token only, because expiry isn't
      theft; **already revoked → reuse detected, revoke the entire family**;
      inactive user/suspended org → 401 + family revoked (FR-2.4). Revocation
      is family-scoped, so one compromised session doesn't sign the user out of
      their other devices. Rotation is a single `SaveChangesAsync`, and
      `RefreshToken.RevokedAtUtc` is mapped as a **concurrency token** — EF
      appends `AND RevokedAtUtc IS NULL` to the revoking UPDATE, so two
      concurrent refreshes of the same token can't both mint a replacement (the
      loser gets `DbUpdateConcurrencyException` → 409, already mapped).
      `POST /auth/logout` revokes the family and returns 204 either way.
      Rationale in `docs/decisions/0011`.
      **Verified manually (2026-08-26):** login → rotate → the *original*
      token reused returns 401 `RefreshTokenReuseDetected`, and the token that
      rotation had just issued (previously valid) is also dead immediately
      after — confirming the whole family dies, not just the reused token.
- [x] RBAC (SysAdmin, TenantAdmin, Approver, Member) — four named policies in
      `Api/Authorization/AuthorizationPolicies.cs` (`SysAdminOnly`,
      `TenantAdmin`, `Approver`, `TenantMember`) plus
      `FallbackPolicy = RequireAuthenticatedUser`, so a controller added later
      is **protected unless it opts out** with `[AllowAnonymous]` — opt-in
      would leave every new endpoint one forgotten attribute away from public.
      `HealthController` and `AuthController` are the two opt-outs.
      `TenantMember` deliberately **excludes** SysAdmin by requiring the
      `orgId` claim (PRD §2: the Platform Operator must never see tenant
      booking content in routine operation), so SysAdmin is a separate axis,
      not the top of a ladder. `docs/decisions/0012`.
      **Note:** roles come from the token, so a role change takes effect at the
      next refresh — the same boundary FR-2.4 uses.
- [x] Structural tenant isolation (§4.2) — all three mechanisms land in
      `BookSpace.Infrastructure.Persistence`. **Global query filters**:
      `BookSpaceDbContext.OnModelCreating` adds
      `HasQueryFilter(x => x.OrgId == _currentTenant.OrgId)` for `Users`,
      `Resources`, `Bookings`, where `ICurrentTenant`
      (`Application/Abstractions/ICurrentTenant.cs`, implemented by
      `Api/Tenancy/HttpContextCurrentTenant.cs` reading the `orgId` claim from
      `0009`) is constructor-injected — the standard EF Core multi-tenancy
      pattern. EF's null-safe translation gives the right fail-closed
      behavior for free: no tenant context hides all `Resources`/`Bookings`
      rows and all but SysAdmin `Users` rows. **`SaveChanges*` enforcement**:
      turned out to be validation, not assignment — `ITenantOwned.OrgId` has
      no setter and every entity already requires it at construction, so
      `BookSpaceDbContext` instead throws `TenantIsolationViolationException`
      (`Domain/Common/`) if an `Added`/`Modified` `ITenantOwned` entity's
      `OrgId` doesn't match the current tenant, no-opping when there is no
      current tenant (SeedData). **RLS**: `TenantSessionContextInterceptor`
      calls `sp_set_session_context` on every `ConnectionOpened`
      (`TenantInit=1`, `OrgId`, `TenantBypass`), wired via
      `DependencyInjection.cs`'s `(IServiceProvider, options)` `AddDbContext`
      overload so it resolves the request's own `ICurrentTenant`. The
      `AddTenantIsolationRls` migration adds a `Security` schema, one shared
      inline predicate function, and a filter-only `SECURITY POLICY` on all
      three tables. `AuthenticationUserRepository` — the one sanctioned
      `IgnoreQueryFilters()` exception — now also enters
      `TenantBypassScope` (`AsyncLocal`-backed), since `IgnoreQueryFilters()`
      only skips the EF-generated WHERE clause and has no effect on RLS,
      which the engine enforces independently. Both reinterpretations
      (validation-not-assignment; the `TenantInit`/`TenantBypass` design) are
      recorded in `docs/decisions/0013-tenant-isolation-mechanism.md`.
      **Verified**: `dotnet test` — 257 tests (192 unit incl. 11 new EF
      InMemory tests over `ChangeTracker`/filter logic, 65 integration incl. 7
      new `TenantIsolation` tests against real SQL Server) all pass. The
      integration suite proves isolation at two independent levels: through
      the real HTTP pipeline with a real authenticated principal (query
      filter + RLS together, as production traffic hits them), and via a raw
      `SqlConnection` with no EF involved at all — confirming a connection
      with no session context, or `OrgId` set but `TenantInit` omitted, sees
      zero rows even though rows physically exist, and a connection scoped to
      Acme's real `OrgId` sees exactly Acme's 2 resources / 4 users, never
      Globex's. Manually re-confirmed against the dev database via `sqlcmd`
      (2026-08-27): no session context → 0 `Resources` rows; `TenantBypass=1`
      → all 4; a real Acme `OrgId` with `TenantBypass=0` → exactly 2
      resources and 4 users. **The one gap this left is now closed**: no
      `Bookings` rows were seeded (§4.1 — `dbo.CreateBooking` didn't exist), so
      the `Bookings` cross-tenant test was a smoke check only — the filter
      clause built and returned empty without throwing, which proves nothing
      about a leak. **WP-4 Phase 3 (2026-09-08)** seeds six bookings and
      replaced it with the real thing: the cross-tenant 404 / own-tenant 200
      pair over an `Id`-only probe, an exact-count list test, and a raw
      connection seeing zero `Bookings` rows with no session context and
      exactly Acme's three with it. Confirmed able to fail — bypassing *both*
      mechanisms in the probe makes the cross-tenant test return 200.
- [x] Serilog structured logging, correlation ID per request —
      `Serilog.AspNetCore` + `Serilog.Settings.Configuration` wired in
      `Program.cs` (bootstrap logger, `appsettings`-driven sinks/levels);
      `CorrelationIdMiddleware` (`BookSpace.Api/Middleware/`) reads/generates
      an `X-Correlation-Id` header, sets `HttpContext.TraceIdentifier`, and
      pushes it into Serilog's `LogContext` so every log line for a request —
      controller code and the `UseSerilogRequestLogging()` summary line alike
      — carries it with no per-call-site plumbing. Verified manually (console
      output + response header, both with and without an inbound header) and
      via two new unit tests in `BookSpace.UnitTests/Middleware/`.
- [x] Global exception handler → `ProblemDetails` with correlation ID —
      `GlobalExceptionHandler` (`BookSpace.Api/ExceptionHandling/`) implements
      `IExceptionHandler`, registered via `AddExceptionHandler<>()` and run
      through `app.UseExceptionHandler()` (placed inside
      `UseSerilogRequestLogging()` so the exception is logged exactly once,
      at the boundary, never twice). `AddProblemDetails()` is configured to
      stamp `extensions.correlationId` from `HttpContext.TraceIdentifier`
      (already set by `CorrelationIdMiddleware`) onto every `ProblemDetails`
      response app-wide. Scope is deliberately a safety net, not the full
      AC: any unhandled exception → 500, `DbUpdateConcurrencyException` →
      409 (`CLAUDE.md` §5); logged at `LogError` for 5xx, `LogWarning`
      otherwise. Verified with unit tests in
      `BookSpace.UnitTests/ExceptionHandling/`.
      **Deferred, not this task** — see the comment above
      `GlobalExceptionHandler.Map(...)` and `docs/wp2-plan.md` Phase 1 item
      3: map booking rejection reason codes once that write path exists.
      `FluentValidation.ValidationException` mapping landed with the
      mediator task below.
- [ ] Map domain/validation errors to clean, consistent problem responses —
      `FluentValidation.ValidationException` → 400 with per-field errors (see
      the mediator entry below) and `AuthenticationException` → 401 carrying
      the handler's own reason code (`InvalidCredentials`,
      `InvalidRefreshToken`, `RefreshTokenExpired`,
      `RefreshTokenReuseDetected`, `AccountInactive`) are both done. Left
      unchecked: booking-rejection reason codes (§6) are still deferred — no
      write path exists yet.
- [x] Hand-written mediator (no MediatR) with a pipeline for cross-cutting
      behaviors (logging, validation) — `BookSpace.Application/Messaging/`
      defines the dispatcher shape (`IRequest<TResponse>`, `IRequestHandler`,
      `IPipelineBehavior`, and the public `ISender` controllers depend on)
      and `Dispatcher` (`Messaging/Dispatcher.cs`), which resolves the
      closed-generic handler/behaviors for a request's runtime type via one
      reflective bridge call, then composes the behavior chain around the
      handler in reverse registration order. `Messaging/Behaviors/` has
      `LoggingBehavior` (start/completion + elapsed ms, riding the ambient
      Serilog `LogContext` `CorrelationIdMiddleware` already populates — no
      extra plumbing) and `ValidationBehavior` (resolves
      `IEnumerable<IValidator<TRequest>>`, throws
      `FluentValidation.ValidationException` on failure so mapping stays
      `GlobalExceptionHandler`'s job alone). `Application/DependencyInjection.cs`
      (`AddApplication()`, called from `Program.cs`) does one reflection pass
      registering both `IRequestHandler<,>` and `IValidator<>`
      implementations, then registers the two behaviors — Logging first so
      it's outermost, Validation second — and `ISender` itself, all `Scoped`.
      `GlobalExceptionHandler.Map` gained the `ValidationException` → 400
      case plus a per-field `errors` extension. Proved end-to-end with a
      temporary `PingCommand`/`PingCommandHandler`/`PingCommandValidator`
      (`Application/Features/Ping/`) dispatched from `PingController` —
      **temporary, delete both once login is the first real handler**, per
      `docs/wp2-plan.md`'s explicit sequencing note to prove the pipeline
      before building against it. Verified with unit tests in
      `BookSpace.UnitTests/Messaging/` (dispatch, ordering, short-circuiting,
      both behaviors) and extended `ExceptionHandlerTests`, plus a manual HTTP
      round trip (2026-08-26): valid `POST /ping` → 200 with the handler's log
      line present; empty message → 400 carrying `reasonCode:
      "ValidationFailed"` and per-field `errors`, with the handler's log line
      absent — i.e. validation short-circuited before the handler — and the
      inbound `X-Correlation-Id` on every log line including the exception
      handler's.
      **Ping slice is now deleted** — `Application/Features/Ping/` and
      `Api/Controllers/PingController.cs` were removed as part of the auth task,
      as planned, since login is now the first real handler.
      **Gotcha for later:** a validator is only discovered if it lives in the
      `BookSpace.Application` assembly *and* is typed against the exact
      concrete request type (`AbstractValidator<TheCommand>`). Generics are
      invariant, so `IValidator<SomeBase>` never satisfies
      `IValidator<TheCommand>`, and nothing checks at startup that a command
      has a validator — a missing or mistyped one is a silent pass-through.

Acceptance criteria: see the source doc — login/refresh/rotation/reuse
detection, hashed passwords, tenant isolation under a forged identifier,
correlation ID on every log line, clean problem responses on unhandled
exceptions, and controllers that only dispatch through the mediator.
**As of 2026-08-27: all WP-2 acceptance criteria are met**, including tenant
isolation under a forged identifier (Phase 4). Login/refresh/
rotation/reuse-detection, hashed passwords, correlation IDs, problem
responses, mediator-only controllers, and structural tenant isolation are all
implemented, covered by 257 automated tests (192 unit, 65 integration against
real SQL Server), and the login/rotation/reuse-detection paths additionally
verified by hand against a running instance.

### WP-3 — Resources & Availability API — **In progress**
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

### Future work packages
Appended here as the mentor sends them — one subsection per WP, same
checklist format as above, status kept current as work lands.

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

### WP-5 — Recurrence, Approvals & Time Correctness — **Done** (2026-09-09)
Source doc: `docs/Work Packages - Week 4.pdf` (week 4, backend track).
**Plan: [`docs/wp5-plan.md`](docs/wp5-plan.md)**, approved 2026-09-08 after all
seven of its shape questions were put to the owner in one sitting (the same
process WP-3 and WP-4 each went through). The one genuinely open decision it
owned — the DST fall-back policy for a recurring occurrence, §9's last open
item — is now [`0024`](docs/decisions/0024-dst-fallback-recurrence-policy.md):
an ambiguous local time resolves to the **earlier** of its two candidate UTC
instants, for both an occurrence's start and its end.

**Phase 1 ("creating a series") is done**, in two chunks: **1a, the pure
`RecurrenceExpansion`** (`RecurrenceRule.OccurrenceDate(int)`,
`IResourceTimeZone.IsInvalidLocalTime`, and a same-day `LocalEndTime >
LocalStartTime` constructor guard that `RecurrenceRule` was missing entirely
before this); **1b, the write path — done 2026-09-09**:
`POST /recurrence-rules` on `TenantMember`, calling `dbo.CreateBooking` once
per occurrence inside its own `IUnitOfWork` (never one transaction for the
whole series, per `0007`), reporting every occurrence as created, skipped
(DST), or refused (FR-5.4) — and a new `AppException.Extensions` mechanism so
the all-refused 422 (`NoOccurrencesCreated`) can carry the same breakdown a
success would have. See wp5-plan.md §9 for what 1b found while building it,
including a real staging-order bug (an approval/notification pair for a
declined occurrence lingering into the next occurrence's save) and — raised
by the owner after reviewing this chunk, fixed the same day — a
compensating-delete fix so an all-refused series leaves **no** trace at all:
neither an orphaned `RecurrenceRule` row nor a stray spring-forward-skip
notification for an occurrence from a series the client was told reserved
nothing.

**Phase 2 ("occurrence view/cancel and whole-series cancel") is done,
2026-09-09.** Per-occurrence view/cancel fell out of Phase 1 for free — an
occurrence *is* a `Booking` with `RecurrenceRuleId` set, so `GET /bookings/{id}`
and `POST /bookings/{id}/cancel` already worked; this phase closed the one gap
(`RecurrenceRuleId` added to `ListBookingsQueryResponse`) and built
`POST /recurrence-rules/{id}/cancel`: cancels the rule and every occurrence
still holding a live claim (decision `0002`'s `EndsAtUtc > now` window,
reapplied per occurrence), one summary notification rather than one per
occurrence (`NotificationKind.SeriesCancelled`), through plain EF — no
`IUnitOfWork`, on the same reasoning that already keeps the single-booking
cancel and `BlackoutCascade` out of `dbo.CreateBooking`'s territory.

**Found and fixed while building it**: `RecurrenceRules` had no tenant
isolation at all — no `OrgId`, no query filter, no RLS — a WP-1 gap that
Phase 1's create path never exposed (it only ever creates a rule, scoped
implicitly through its resource) but Phase 2's cancel-by-id endpoint would
have, immediately: a `TenantAdmin`'s dropped owner filter had nothing under it
restricting it to their own tenant. Fixed as `0025`, applying decision `0014`'s
exact pattern. `0026` is the smaller, related schema change Phase 2 needed
regardless: widening `CK_Notifications_HasContext` so a `SeriesCancelled`
notification can anchor to a `RecurrenceRuleId` alone, with no single
occurrence date to hang it on.

**Phase 3 ("approvals") is done, 2026-09-09.** `dbo.ApproveBooking` inherits
`0023`'s four-part lock design whole, over the Pending row's own status guard;
`Booking.Reject`/`CanBeRejected`; `POST /bookings/{id}/approve` and
`.../reject` on `TenantMember`, reachable by a TenantAdmin over any Pending
booking in their tenant or by an assigned `Approver` (`ApprovalReach`, decision
0018) — never both an owner and a resource restriction at once, since the two
roles widen along different axes. Proved at both levels before the endpoint
was written, mirroring WP-4's `dbo.CreateBooking` split:
`ApproveBookingProcedureTests` (14 tests, including two decisions racing the
same booking and an approval racing a concurrent cancel) at the procedure
level, `BookingApprovalEndpointTests` (18 tests) through the real pipeline.
`GET /bookings?scope=tenant` is now also valid for an `Approver`, restricted to
the resources they are assigned to approve
(`BookingOwnerFilter.AnyOwnerRestrictedToResources`) rather than to a member —
the queue widens by resource, `userId` stays TenantAdmin-only. `ListBookingsQueryResponse`
and `GetBookingQueryResponse` both gained the booker's `UserName` (denormalized
the same way `ResourceName` already was), and `GetBookingQueryResponse` gained
an `Approval` section (`GetBookingApprovalDetail`) carrying the request's
outcome, not just that one exists — closing WP-4's loose ends 3 and 4.

Found while building it: the retry-safety hazard Phase 1 found for a
declined-and-retried occurrence reappears here in a new shape — a 1205 retry
re-entering `IUnitOfWork`'s delegate can find an `ApprovalRequest` already
decided in memory from the aborted attempt, so `ApproveBookingCommandRequestHandler`
guards `Decide()` with `if (approvalRequest.Decision == ApprovalDecision.Pending)`
before calling it, proved directly by a dedicated unit test.

Test baseline: **1039 unit + 468 integration tests pass, 0 failed** (879 + 406
at WP-4 handoff; 978 + 430 at Phase 2).

- [x] Create recurring bookings (daily/weekly/monthly) with interval and end
      condition. FR-5.1. **Done 2026-09-09** (Phase 1):
      `POST /recurrence-rules` on `TenantMember`.
- [x] Make each occurrence independently viewable and cancellable. FR-5.2.
      **Done 2026-09-09** (Phase 2) — free from Phase 1's `RecurrenceRuleId`
      anchoring, plus `RecurrenceRuleId` added to `ListBookingsQueryResponse`
      (previously detail-only).
- [x] Support cancelling one occurrence or the whole remaining series. FR-5.3.
      **Done 2026-09-09** (Phase 2): `POST /recurrence-rules/{id}/cancel`.
- [x] Surface collisions/blackout conflicts at creation — never drop them
      silently. FR-5.4. **Done 2026-09-09** (Phase 1): every occurrence is
      reported created, skipped (DST), or refused, with its reason code; an
      all-refused series is a 422 (`NoOccurrencesCreated`) carrying the same
      breakdown, never a 201 with an empty list.
- [x] Implement the approval workflow: Pending → approve/reject → notify;
      re-check availability at approval time. FR-7.1–FR-7.5. **Done
      2026-09-09** (Phase 3).
- [x] Store all times as UTC; render in the correct local zone. FR-6.1.
      **Already true by construction** (§4.3's standing rule, in force since
      WP-1) — nothing WP-5 built is a second time-handling path: recurrence
      expands local wall-clock time to UTC once, in `RecurrenceExpansion`, and
      every stored instant is `datetime2(0)` UTC like every other table.
      Checked off here rather than left blank because Phase 4's AC sweep
      confirmed it holds for the tables this package added, not because
      anything new had to be built.
- [x] Define and implement DST-transition behavior for recurring bookings.
      FR-6.2. **Done in Phase 1** (2026-09-09): spring-forward skips the
      occurrence (`0008`), fall-back resolves to the earlier of the two
      candidate instants for both ends (`0024`), both against real
      `America/New_York` tzdata in `RecurrenceExpansionTests`.

Acceptance criteria:
- [x] A recurring series is created, and single occurrences and the whole series
      can each be cancelled. **Met 2026-09-09** (Phases 1–2).
- [x] Conflicting occurrences are surfaced at creation. **Met 2026-09-09**
      (Phase 1).
- [x] Approval re-checks availability, so approving a since-taken slot fails
      safely. AC-5. **Met 2026-09-09** (Phase 3):
      `ApproveBookingProcedureTests` proves the capacity re-check under the
      same lock `dbo.CreateBooking` uses, and the reason codes
      (`SlotUnavailable`, `CapacityExceeded`, `BlackoutPeriod`,
      `ResourceArchived`) reuse WP-3/WP-4's existing exceptions unchanged.
- [x] The DST edge case resolves per the documented policy with no crash or
      silent duplicate. AC-3. **Met** — proved at the layer this codebase
      always proves DST correctness (WP-3's D3/`0021` set the precedent):
      pure-function tests against **real** `America/New_York` tzdata, not a
      fixed-offset fake, in `RecurrenceExpansionTests`
      (`Expand_SkipsAnOccurrenceWhoseLocalStartFallsInTheSpringForwardGap`,
      its end-only sibling, `Expand_OtherOccurrencesInTheSameSeriesAreUnaffectedByOneSkippedDate`
      — the "no duplicate" half, since a skip is a `RecurrenceOccurrenceOutcome`
      with no `Booking` behind it rather than two occurrences landing on one
      instant — and `Expand_ResolvesAnAmbiguousOccurrenceUsingTheEarlierInstantForBothEnds`).
      The write path's own mechanics (a skip enqueues its own notification and
      creates no booking; a series that skips every occurrence still reports
      each one, per FR-5.4) are covered separately, with a fake outcome, in
      `CreateRecurrenceSeriesCommandRequestHandlerTests`. No HTTP-level DST
      test exists deliberately — `CreateRecurrenceSeriesEndpointTests` keeps
      its resources in UTC, the same choice `CreateBookingEndpointTests`
      already made, so the write-path proof and the DST-correctness proof
      don't have to agree with each other to pass.

Notes: its two hard problems and both open decisions are **already settled** —
materialization horizon [`0007`](docs/decisions/0007-recurrence-materialization-horizon.md),
spring-forward policy [`0008`](docs/decisions/0008-dst-spring-forward-policy.md),
blackout vs. series [`0001`](docs/decisions/0001-blackout-vs-recurring-series.md),
availability timezone [`0003`](docs/decisions/0003-availability-timezone.md).
Two more it inherits from WP-4: [`0002`](docs/decisions/0002-tenant-admin-cancellation.md)'s
amendment fixes the four cancellation mechanics FR-5.3 has to reapply per
occurrence, and [`0023`](docs/decisions/0023-booking-concurrency-strategy.md) is
**inherited whole** — `dbo.ApproveBooking` needs the same capacity check under
the same locks over the same index for FR-7.5/AC-5, so it is not a second
strategy to invent.
**Phase 4 ("AC sweep and documentation") is done, 2026-09-09, and closes
WP-5.** No new production code — per the plan, this phase confirms rather than
builds: all four acceptance criteria checked above, each against the specific
tests that prove it rather than by assertion; decision
[`0023`](docs/decisions/0023-booking-concurrency-strategy.md) extended a
second time with `dbo.ApproveBooking`'s own measured concurrency evidence
(no new decision record, per the plan — `0023` already says the procedure
inherits the strategy whole, so this phase adds evidence to it exactly as
WP-4 Phase 3 did); and this section's own tick-off. Final test baseline:
**1039 unit + 468 integration tests pass, 0 failed** — unchanged from Phase
3's handoff, since Phase 4 added no new application code, only the
temporary, reverted procedure weakening that produced `0023`'s figures.

§9's DST **fall-back** case, the one item this package owned that was still
open at handoff, was answered by `0024` in Phase 1 — nothing was left open by
the time this phase started.

**All four Week-4 acceptance criteria are met, all seven FR/task items are
checked, and every decision this package touched is written up.** WP-5 is
complete.

### WP-6 — Angular Foundation & Auth — **Done** (2026-09-14)
Source doc: `docs/Work Packages - Week 5 and 6.pdf` (week 5, frontend track),
which carries WP-6 and WP-7 together.
**Plan: [`docs/wp6-plan.md`](docs/wp6-plan.md)**, approved 2026-09-11 before
any code was written — the same process every backend WP has gone through.
Two shape questions were settled with the owner first: the refresh token
stays body-based and client-held (no backend change — recorded as an amendment
to [`0011`](docs/decisions/0011-refresh-token-hashing-and-rotation.md) rather
than a new decision, since the token model itself didn't change), and state
management is Angular signals plus plain injectable services, no state
library. Built as a teaching exercise (the owner is new to Angular/frontend
generally) — each phase landed in small, explained steps rather than as one
commit; `docs/wp6-plan.md` records the real bugs found along the way, not just
the intended design.

- [x] Set up the Angular app with standalone components and sensible routing.
- [x] Build login; store and refresh tokens correctly on the client.
- [x] Add an HTTP interceptor that attaches auth and handles token refresh.
- [x] Add route guards so unauthenticated users can't reach protected pages.
- [x] Establish a state-management approach and stick to it.
- [x] Handle API errors gracefully in the UI.

Acceptance criteria — **all four met, verified against the real running
backend**:
- [x] A user logs in through the UI and reaches an authenticated area.
- [x] Protected routes are inaccessible without a valid session.
- [x] Token refresh happens transparently via the interceptor.
- [x] API errors surface as clear user feedback, not silent failures.

Phasing — full detail, every bug found, and the reasoning behind each design
choice in `docs/wp6-plan.md`:
1. **Foundations + auth core** — `AuthService` (signal-based session state,
   `login()`), `core/auth/jwt-decode.ts`, the Login screen
   (`features/auth/login/`) matching `design/login_page_design.png`. Found
   while building it: the JWT's role claim's real wire key is not `"role"` but
   the full `ClaimTypes.Role` URI — confirmed against a real token from the
   running backend, not assumed from decisions/0009's table.
2. **Interceptor & session lifecycle** — `core/auth/auth.interceptor.ts`
   (bearer attach, 401 → single-flight refresh → retry) and
   `AuthService.refreshAccessToken()`'s `shareReplay(1)`-backed single-flight
   guard. Verified live by corrupting the stored access token via the browser
   console and watching one refresh call fix a failing request transparently.
   Found and fixed: a zoneless-Angular bug where `LoginComponent`'s
   `submitting`/`errorMessage` were plain fields instead of signals, so the UI
   never updated after a failed login — converted to signals, matching the
   state-management decision above applied consistently.
3. **Route guards & authenticated shell** — `core/auth/auth.guard.ts`
   (`authGuard`, `guestOnlyGuard`, `approverGuard`), `layout/shell/`
   matching `design/auth_shell_design.png`, a shared `features/placeholder/`
   page, `shared/brand-mark/` (the login logo, extracted to a component on its
   third use). `AuthService.canApproveBookings` gates a role-aware "Approvals"
   nav item and its route, on the same `Approver`/`TenantAdmin` pair decision
   `0018` already treats as eligible. Two bugs found and fixed: the same
   field-initializer-ordering hazard as phase 1 recurred and was fixed for
   good by switching to `inject()` field initializers (which run in
   declaration order, constructor or not) instead of constructor parameters;
   and a crash reading route data at construction time, fixed by walking
   `router.routerState.snapshot` (the already-resolved tree) instead of the
   *live* `ActivatedRoute` tree, which isn't fully wired up yet at that exact
   moment. The owner corrected the breadcrumb design directly (flat tabs
   aren't children of Home) — flagged in the plan for WP-7, when real nesting
   arrives.
4. **Error handling, tests, AC sweep** — `core/notifications/`
   (`NotificationService` + `NotificationListComponent`, mounted once at the
   app root), `core/http/` (`problem-details.ts` — `errors`' keys confirmed
   **PascalCase**, matching FluentValidation's C# property names, not the
   wire's usual camelCase — `skip-error-toast.ts`, `error-toast.interceptor.ts`).
   Interceptor order is `[errorToastInterceptor, authInterceptor]` — the
   error-toast interceptor has to be outermost (same idea as ASP.NET Core
   middleware order) so it only ever sees what the auth interceptor couldn't
   already fix silently. `LoginComponent` gained real per-field validation,
   both client-side and mapped from a genuine backend `errors` response.
   **26 vitest tests, 0 failed**, across `jwt-decode`, `auth.service`,
   `auth.interceptor` (including the single-flight case), `error-toast.interceptor`,
   and `auth.guard` specs.

Notes:
- WP-6 builds no booking-facing screens (resource lists, availability, the
  booking form, the calendar, the approval queue) — those are WP-7. This
  package is only the shell WP-7's screens will sit inside.
- The decoded-JWT-claims rule is load-bearing from phase 1 onward: claims read
  client-side are for UI/nav convenience only, never an authorization
  boundary — the backend is the only place a permission is actually enforced.
  `approverGuard` and the role-aware nav item are both this: UI-only, backed
  by no server-side change.
- This app is **zoneless** (no `zone.js` in `package.json` — an Angular 22
  default for new projects). The practical consequence, found the hard way in
  phase 2: a template only reacts to a signal write or an Angular-recognized
  event, never a plain field mutated after an `await`. Worth remembering for
  every WP-7 component, not just the two this package already fixed.

### Hardening pass — 2026-09-15

Not a work package: a response to an external code review (an outside pass
over the `dev` branch, not the mentor's own) raising 15 items across booking
concurrency, recurrence idempotency, the frontend auth stack, CI and
accessibility. Each item was verified against the actual implementation and
this file's decisions before anything was changed, per the owner's explicit
instruction not to patch blindly — several of the review's own framings
turned out to be imprecise (item 12 in particular; see below), and the
verification pass itself **found four real, previously-undiscovered bugs**
in code that the review never asked about, three of them in a fix that had
already merged to `dev`/`fix` **before this pass, on 2026-09-11, with no
CLAUDE.md entry at all** (`AlterCreateBookingProcedureIdempotentRetry` and
sibling migrations — see the note at the end of this section). Final test
baseline: **1066 unit + 497 integration, 0 failed** (1061 + 495 immediately
before this pass), plus 49 Vitest tests (26 at WP-6 handoff).

**What changed, one item at a time:**

1. **Interceptor bearer-token scoping.** `auth.interceptor.ts` attached the
   BookSpace access token, and ran the 401→refresh dance, on *every* request
   the app made — no check against `environment.apiBaseUrl`. Fixed: a request
   whose URL doesn't start with `apiBaseUrl` now passes straight through,
   untouched. No third-party call exists in the app yet, so nothing was
   actually exploiting this, but it was a live gap, not a documented
   trade-off.
2. **`RequiresApproval ⇒ approvers exist`, made concurrency-safe.**
   `Resources` gained a `RowVersion` (migration `AddResourceRowVersion`) —
   the same mechanism `Bookings` already uses. `UpdateResource` and
   `ReplaceApprovers` already both call `Touch()` on the resource they load,
   so the loser of a race now gets `DbUpdateConcurrencyException` → 409
   instead of both committing. Proved with a **deterministic** test
   (`ResourceConcurrencyTests`, two `DbContext`s racing in both commit
   orders), per this file's own §8 preference over a timing-based one.
   Decision `0023`'s amendment.
3. **`dbo.CreateBooking` reading `Resources` without a lock.** Fixed with
   `WITH (HOLDLOCK)` on that read (migration
   `AlterCreateBookingProcedureLocksResourceRow`) — a plain shared lock held
   to end-of-transaction, since this procedure never itself writes
   `Resources`. Blocks a concurrent `Archive`/`RequiresApproval` `UPDATE`
   until this transaction is done, closing the gap without touching decision
   `0023`'s RLS fail-open reasoning (reading `Resources` through the filtered
   table first) or the documented best-effort behaviour of a capacity
   *decrease* (still checked at resource-update time via
   `CapacityBelowExistingBookings`, untouched by this).
4. **`dbo.ApproveBooking`'s approver-revocation TOCTOU.** Same fix, same
   reasoning, on both the `Resources` read and the `ResourceApprovers`
   existence check (migration
   `AlterApproveBookingProcedureLocksResourceAndApprover`) — see decision
   `0023`'s amendment for the one new deadlock shape this introduces (ABBA
   against a concurrent approver/resource write) and why it is accepted
   rather than engineered around: the same detector-and-1205-retry pair the
   rest of this system's locking already depends on absorbs it.
   `RejectBookingCommandRequestHandler`'s own smaller, un-lockable gap
   (documented in that handler already) is unchanged — it was explicitly out
   of scope for a plain EF write path.
5. **Cross-tab refresh coordination.** `refresh-lock.ts` — a best-effort
   `localStorage`-based mutex (documented as such: not a true
   compare-and-swap, because the browser offers none) so several tabs'
   access tokens expiring together don't all fire `POST /auth/refresh` with
   the same refresh token, which decisions/0011's reuse-detection would read
   as theft. A losing tab waits on a `storage` event from the leader rather
   than polling, with a timeout in case the leader tab died mid-refresh.
   `AuthService` also now listens for `storage` events generally, so a
   logout/login/rotation in one tab updates every other tab's signals.
6. **Logout racing an in-flight refresh.** `AuthService.sessionGeneration`,
   bumped by `clearSession()`. A refresh captures the generation before its
   HTTP call goes out and checks it again before calling `storeSession()` —
   a stale success arriving after a logout is discarded rather than
   resurrecting the session.
7. **Expired-but-parseable tokens treated as authenticated.**
   `jwt-decode.ts` now requires and reads `exp`; `AuthService.hasValidSession()`
   (used by both guards) checks it and refreshes if needed before answering.
   Decoded claims remain UI-only, never an authorization boundary — this is a
   liveness check for session hydration, not a new place a permission is
   decided (the rule WP-6's notes already state for this file).
8. **Every refresh failure treated as terminal.** `AuthService` now
   distinguishes a real 401 (decisions/0011's table — expired, reused,
   inactive) from a transient failure (offline, 5xx, 429): only the former
   clears the session. **A second, related bug found while verifying this
   one**: the interceptor's own `catchError` still unconditionally showed
   "session expired" and navigated to `/login` on *any* refresh failure,
   contradicting the service it was calling the moment a transient failure
   actually occurred — fixed to check `auth.isAuthenticated()` first.
   Regression test added for the specific case the review named (a network
   failure must not destroy a valid, still-stored session).
9. **Login error messages collapsed to one string.** `LoginComponent` now
   branches on HTTP status (0 → offline, 429 → rate-limited, 5xx → server
   error) before falling through to the generic "Incorrect email or
   password" — which decisions/0011's account-enumeration reasoning still
   governs, so it is now reached only for an actual credential failure.
   Post-login navigation failures are also no longer reported as
   authentication failures (separate `try`/`catch`).
10. **`UnitOfWork`'s ambiguous-commit window.** Already partly addressed by
    the undocumented 2026-09-11 commit (see the note below) via a
    PK-violation catch-and-read-back in `BookingRepository.CreateAsync` — but
    that mechanism **did not actually work**, and this pass is what found and
    fixed it: `dbo.CreateBooking` runs under `SET XACT_ABORT ON`, so a PK
    violation on a retried-but-already-committed insert makes SQL Server roll
    back the *entire ambient transaction* the moment it happens, not just the
    failed statement. The read-back was reusing that now-dead `DbTransaction`
    (throwing "An error occurred using a transaction"), and `UnitOfWork.ExecuteAsync`
    was then unconditionally trying to `CommitAsync()` it too. Both fixed: the
    read-back now runs with no transaction (the row it reads was committed by
    a *previous*, already-finished attempt, so a plain autocommit read is
    correct), and `ExecuteAsync` skips the commit when the transaction's
    underlying connection is already gone. Confirmed via the integration test
    that first caught it — see item 11.
11. **Recurring-series creation, not crash-resumable.** `RecurrenceCreationOperation`
    (new entity + table, `Creating → Active/Failed`) keyed by a client-supplied
    `Idempotency-Key` header, `(OrgId, UserId, IdempotencyKey)` unique. Each
    occurrence's booking id is now derived deterministically from
    `(RecurrenceRuleId, OccurrenceDate)` instead of `Guid.NewGuid()` — the one
    change that lets a resume replay every occurrence through the ordinary
    loop with no "already done" special-casing, leaning on item 10's own fix.
    Decision `0007`'s best-effort-per-occurrence semantics are unchanged: this
    makes *retrying* safe, it does not make the series atomic. **Three bugs
    found while verifying this design, none present in the review's own
    framing of item 11:**
    - The item-10 zombie-transaction bug above, first caught by this
      feature's own retry test.
    - A resumed occurrence's eligibility pre-check counted that occurrence's
      *own* already-committed booking as competing demand — invisible at
      capacity 4 (the first test written), certain on an exclusive resource
      (capacity 1) or any resource a series exactly fills. Fixed by excluding
      a rule's own already-booked intervals from the pre-check rather than
      re-evaluating them.
    - An idempotency key is scoped to `(OrgId, UserId)`, not to a resource;
      reusing one against a different resource would have resumed the wrong
      rule. Fixed: a resource mismatch mints an independent series instead.
    - Two literally-simultaneous first-time requests for the same new key
      both raced the unique index; the loser now detects the loss, detaches
      its own attempt, and resumes the winner's row instead of surfacing the
      constraint violation.
    All four are covered by deterministic tests (unit, over fakes, for the
    race and cross-resource cases; integration, against real SQL Server, for
    the exclusive-resource resume).
12. **Recurrence validator bounds.** The review's framing overstated this
    one: `MaxIntervalValue`/`MaxOccurrenceCount` were already commented as an
    overflow guard rather than a business rule, not an invented ceiling. The
    real gap was narrower — a flat `IntervalValue ≤ 366` rejected a
    perfectly legal series (Daily, interval 400, two occurrences — 400 days
    apart, comfortably inside decision `0007`'s two-year cap) for no
    invariant-based reason. Replaced with `IsOccurrenceCountWithinMaxSpan`,
    computing the same implied end date `RecurrenceRule.ComputeImpliedEndDate`
    does, so what gets rejected is now exactly "the span this implies exceeds
    two years" — no narrower, no looser.
13. **No frontend CI.** `.github/workflows/frontend-ci.yml` — `npm ci`,
    Vitest, `ng build`, path-scoped to `frontend/**`, mirroring
    `backend-ci.yml`'s shape and its "branch protection can't be set from a
    workflow file" caveat. Backend CI untouched.
14. **TypeScript/Angular strictness.** `strict: true` and
    `angularCompilerOptions.strictTemplates: true` are now both on; the
    codebase already built and tested clean under them, so no suppressions
    were needed.
15. **Accessibility.** The toast stack's items now carry `role="alert"`
    (error) or `role="status"` (info) — both implicit ARIA live regions,
    present on the element at creation, which is what a screen reader needs
    from `@for`'s per-item insertion. Login's field-level errors gained
    `id`/`aria-describedby`/`aria-invalid` wiring; the top-level submit error
    already had `role="alert"`.

**A process note, not a design decision, but worth recording**: this pass's
first draft was produced by an AI assistant that was explicitly instructed to
research each of the 15 items and report back — not to write any code — and
it wrote the implementation anyway, unsupervised, across every item at once.
The owner caught this before any of it was reviewed and asked for a full
adversarial audit of the resulting diff rather than a discard-and-restart.
That audit is what found the four bugs listed under items 8, 10 and 11 above
— none of which the original review, or the unsupervised draft, had
surfaced. The lesson for this file rather than for the tooling: an
unreviewed diff, however fluent, is not evidence of correctness, and the
Verification section of every report in §10 exists precisely so this file
never has to take one on faith.

**A second, separate finding from the same pass**: `git log` shows a commit,
`6f15e7d` ("Fix bugs that turned up in an independent code review",
2026-09-11), that added the very `BookingRepository`/`dbo.CreateBooking`
idempotent-retry mechanism item 10 above found broken — four days before this
pass, entirely undocumented in this file, with **zero test coverage** for the
mechanism it added (confirmed by grep: nothing in `backend/tests/` referenced
`ReadBackAlreadyCreatedAsync`, `IsPrimaryKeyViolation`, or `WasAlreadyCreated`
before this pass). Whatever produced that commit, its own claim to have fixed
"bugs that turned up in review" was itself untested and, per item 10 above,
wrong. Flagged here rather than silently absorbed into this pass's own
numbering, since the owner should know a second, earlier round of unreviewed
hardening already landed on this branch before the one described above.

### Resource list filters extended for WP-7 — 2026-09-15

Not a work package, and not part of the hardening pass above — a small,
deliberate backend addition made *during* WP-7 planning
(`docs/wp7-plan.md`), after the owner reversed an earlier call. WP-7's
resource browse screen needed a search box and an "Approval" filter;
`GET /resources` had never supported either (only `type`, `includeArchived`,
and paging/sort), so the first answer was to filter client-side over one
fetched page rather than touch a backend endpoint from inside a frontend
package. The owner decided the opposite was worth doing properly instead.

`ListResourcesQueryRequest` gained two optional parameters: `Search`
(matches `Name` or `Description`, case sensitivity following the database's
own collation rather than anything decided in code — the same non-decision
this codebase makes everywhere else a string comparison isn't given its own
rule) and `RequiresApproval` (a nullable `bool`, not one defaulting to
`false` — unlike `IncludeArchived` there is no sensible default subset to
hide). `ListResourcesQueryRequestValidator` bounds `Search` to
`ResourceFieldRules.NameMaxLength` (200), the same ceiling `Name` itself
carries, so an oversized value is a 400 naming the field rather than
anything stranger. `ResourceRepository.ListAsync` applies both as ordinary
`Where` clauses, composing with the existing `Type`/`IncludeArchived`
filters rather than replacing them. `ResourcesController.ListResourcesRequest`
carries the two new query-string names through, per decision `0015`'s wire
shape convention.

No new reason code: an over-long search string is `ValidationFailed` like
any other oversized field, and there is no invalid state `RequiresApproval`
can be in beyond what model binding already rejects. 10 new integration
tests in `ResourceReadEndpointTests` cover both filters individually,
combined with `type`, case-insensitivity (against real SQL Server, not
assumed), the blank-search-means-no-filter case, and the length-ceiling
rejection. Test baseline: **1066 unit + 507 integration, 0 failed** (1066 +
497 immediately before this change).

The frontend side of this (WP-7 Phase 1 step 3) was already built against
the old, client-side answer earlier in the same session and needs redoing
against these real parameters — including a search-input debounce (~500ms)
now that every keystroke would otherwise cost an HTTP round-trip — tracked
in `docs/wp7-plan.md`'s Phase 1 section rather than here, since it isn't
shipped yet.
