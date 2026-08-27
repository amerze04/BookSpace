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

- Global query filters on `Users`, `Resources`, `Bookings` in `OnModelCreating`
- `OrgId` set in `SaveChangesAsync` for added `ITenantOwned` entities
- SQL Server row-level security via a connection interceptor calling
  `sp_set_session_context`

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
| 1 | Database constraints | Interval sanity, status domains, uniqueness, composite tenant FK |
| 2 | Locking protocol | No overbooking beyond capacity, approval re-check |
| 3 | RLS + query filters | Tenant isolation |
| 4 | Application code | Availability windows, blackouts, duration limits, approval routing |

Rule of thumb: PRD wording of "must never" belongs in tier 1–3. "Should"
belongs in tier 4.

Rejections return a machine-readable reason code, not just a message
(FR-4.5): `SlotUnavailable`, `CapacityExceeded`, `OutsideAvailability`,
`BlackoutPeriod`, `ResourceArchived`, `ApprovalRequired`.

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

**Still open** — flag before building the affected feature, don't decide
silently: the DST **fall-back** case (clocks go back, a local time occurs
twice and is ambiguous rather than nonexistent). See the Notes section of
`0008` for why it's a genuinely separate question from spring-forward.

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
- `backend/src/BookSpace.Domain/Entities/Booking.cs` — created — booking entity with status enum and rowversion
- `backend/src/BookSpace.Infrastructure/Persistence/BookSpaceDbContext.cs` — modified — added Bookings DbSet, global query filter
- `backend/src/BookSpace.Api/Controllers/BookingsController.cs` — modified — POST endpoint now returns 409 on SlotUnavailable

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

### Future work packages
Appended here as the mentor sends them — one subsection per WP, same
checklist format as above, status kept current as work lands.
