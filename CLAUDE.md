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

The procedure takes `UPDLOCK, HOLDLOCK` range locks and compares the sum of
overlapping `Quantity` against `Resources.Capacity`. LINQ cannot express those
hints, and SQL Server has no exclusion constraint, so this is the only place
the guarantee exists (FR-4.2, FR-7.5, AC-1).

`IX_Bookings_Resource_Start` is load-bearing — it is what keeps the range lock
narrow instead of table-wide. Never drop it. If you change the overlap
predicate, re-check the execution plan seeks rather than scans.

Approval re-runs the same capacity check. A slot may have been taken since the
request was made (FR-7.5, AC-5).

### 4.2 Tenant isolation is structural, not a WHERE clause

FR-1.2 requires isolation that cannot be bypassed by forgetting a filter.
Three mechanisms, all required:

- Global query filters on `Users`, `Resources`, `Bookings`,
  `AvailabilityWindows`, `BlackoutPeriods` in `OnModelCreating`
- `OrgId` set in `SaveChangesAsync` for added `ITenantOwned` entities
- SQL Server row-level security via a connection interceptor calling
  `sp_set_session_context`, with `Security.TenantAccessPolicy` covering the
  same five tables

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

Status values match the PRD exactly: `Pending`, `Confirmed`, `Rejected`,
`Cancelled`, `Completed`, `NoShow`. Note **Rejected**, not Declined.

---

## 6. Validation tiers

Put each rule in the right tier. Most bugs here come from putting a "must
never" in tier 4.

| Tier | Enforced by | Contains |
|---|---|---|
| 1 | Database constraints | Interval sanity (a booking's, and a resource's duration bounds), status domains, uniqueness, composite tenant FK |
| 2 | Locking protocol | No overbooking beyond capacity, approval re-check |
| 3 | RLS + query filters | Tenant isolation |
| 4 | Application code | Availability windows, blackouts, a booking's length against the resource's duration limits, approval routing |

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
`ResourceArchived`, `ApprovalRequired`.

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
3. [`0003`](docs/decisions/0003-availability-timezone.md) — Availability is
   expressed in the resource's timezone, not the booker's.
4. [`0004`](docs/decisions/0004-no-show-definition.md) — No-show = `Confirmed`,
   never checked in, past `StartsAtUtc + Organizations.NoShowGraceMinutes`.
   Determined by the no-show release job (system-initiated, not a person —
   `Bookings.UpdatedByUserId` stays null on this transition).
5. [`0005`](docs/decisions/0005-capacity-semantics.md) — `Capacity` means
   concurrent units, not seats within one exclusive booking.
   `Bookings.Quantity` sums against it.
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

**Decided but not yet written up as numbered records** — two WP-3 decisions
(D2, D3) were settled by the repo owner on 2026-08-28 before that package
started, and live in `docs/wp3-plan.md` until the phase implementing them
lands (Phase 5) and promotes them to the next free numbers (`0020`+, since Phase 3's approver eligibility took `0018` and Phase 4's lifecycle took `0019`):
interval-plus-`remainingCapacity` slot semantics, and DST handling for
availability *ranges*. Treat them as settled, not open. D1 was promoted to
`0014` when WP-3 Phase 1 landed, and D4 to `0017` when Phase 2 did.

**Still open** — flag before building the affected feature, don't decide
silently: the DST **fall-back** case (clocks go back, a local time occurs
twice and is ambiguous rather than nonexistent). See the Notes section of
`0008` for why it's a genuinely separate question from spring-forward.
WP-3's D3 resolves this for availability *windows* (a range absorbs a missing
or repeated hour); it leaves the *occurrence* case — an instant, which has to
land somewhere — exactly as open as it was.

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
- Seed data stops short of `Bookings`, `ApprovalRequests`, `Notifications`,
  and `RefreshTokens` — the first three because CLAUDE.md §4.1 requires
  Booking writes to go through `dbo.CreateBooking`/`dbo.ApproveBooking`, which
  don't exist yet; the last because refresh tokens are issued at login, not
  meaningful as static data.
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
      resources and 4 users. **Known gap, not silently skipped**: no
      `Bookings` rows are seeded yet (§4.1 — `dbo.CreateBooking` doesn't
      exist), so the `Bookings` cross-tenant test is a smoke check only (the
      filter clause builds and returns empty without throwing); the real leak
      test is deferred to whatever work adds a Bookings write/read path.
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
- [ ] Build an availability query: given a resource and date range, return
      bookable slots.
- [ ] Design clean DTOs, error contracts, and pagination — pagination and the
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

Acceptance criteria (source doc):
- [x] An admin can publish a resource with availability and blackout rules —
      **met 2026-09-02**, when Phase 4 supplied the blackout half. An admin
      creates a resource, gives it a weekly schedule, assigns approvers and blacks
      out spans on it, and a member of the same tenant immediately sees all four
      (`ResourceAcceptanceTests`, `AvailabilityWindowEndpointTests`,
      `ApproverEndpointTests`, `BlackoutPeriodEndpointTests`).
- [ ] The availability query correctly excludes blackout periods and existing
      bookings.
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
   **Plan approved by the owner 2026-09-03; steps 1 and 2 of 4 done the same day** —
   full detail in `docs/wp3-plan.md`, which is the document to read before
   continuing. In brief:
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
     `0001`'s cascade); and zero-capacity spans are dropped **after** adjacent
     equal-capacity spans are rejoined, since the other order would rejoin across
     a fully-booked gap and report time that is not free.
   - **Q5's minimum-duration floor has a consequence the owner may want to
     revisit.** It is applied per interval, as specified — but the sweep splits at
     every capacity change, so a 1-unit booking mid-day leaves three intervals and
     the flanking two can fall under the floor and vanish, even though a 1-unit
     booking across the whole run *would* be accepted. Inherent in D2's
     one-figure-per-interval shape, implemented as specified, recorded by a test,
     and written up in `docs/wp3-plan.md`. Only bites a resource that has a
     `MinDurationMinutes` **and** partially-consumed capacity, so it does not
     block step 3.
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

### Future work packages
Appended here as the mentor sends them — one subsection per WP, same
checklist format as above, status kept current as work lands.
