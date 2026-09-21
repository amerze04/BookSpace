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
- [`STATE-OF-THE-APP.md`](docs/STATE-OF-THE-APP.md) — **a current snapshot of
  what works, what is verified, what is deliberately absent, and what is left.**
  Start there when picking this up cold or preparing a demo; updated at the
  close of each phase.
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

### Frontend folder layout

Restructured 2026-09-18 on the owner's instruction. Every feature is split by
**what a file is**, not by which screen happens to use it:

```
frontend/src/app/
  tests/                       app-level specs
  core/                        auth/ · http/ · notifications/, each + tests/
  features/<feature>/
    components/<component>/    one folder per component: its .ts/.html/.scss
                               and nothing else
    services/                  thin API services
    models/                    wire types mirroring the backend DTOs
    <purpose>/                 helpers grouped by what they do — e.g.
                               availability/grid, availability/date,
                               booking/arrival, booking/rejection,
                               booking/recurrence, calendar/grid
    tests/                     every .spec.ts for that feature
  layout/                      components/shell/ · breadcrumb.service.ts · tests/
  shared/                      brand-mark/ · resource-type/
```

Two rules, and the second is not negotiable:

- **A component gets its own folder under `components/`**, named without the
  `.component` suffix, holding only its three files. They travel together, so
  `templateUrl`/`styleUrl` stay `./<name>.component.html`.
- **No `.spec.ts` ever sits outside a `tests/` folder.** A spec should not be
  visible without opening a tests folder first. Vitest globs
  `src/**/*.spec.ts`, so location is a convention this file enforces rather
  than something the build checks — a new spec put beside its subject will
  still run, and is still wrong.

Paths quoted in `docs/roadmap/wp*.md` before 2026-09-18 predate this move.

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
than was asked for** is `CapacityExceeded` (owner's call, 2026-09-07).

**Corrected 2026-09-17, while building WP-7 Phase 3.** This paragraph used to
end "an exclusive resource can therefore only ever produce the first, since
`Capacity = 1` admits no quantity but 1" — which holds for what can *succeed*,
not for what a client can *send*. `CreateBookingCommandRequestValidator`
deliberately puts no upper bound on `Quantity` ("what is too many depends on
the resource's `Capacity`, which this cannot see"), so a request for 3 units of
a one-unit resource is well-formed, reaches `dbo.CreateBooking`, and comes back
`CapacityExceeded` — verified against the running API, not reasoned about. The
split itself is unchanged: an exclusive resource asked for the only quantity it
can accept still answers `SlotUnavailable`.

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
more. All are resolved, written up individually in `docs/decisions/` — read
the linked doc before touching the affected feature, the reasoning matters
as much as the answer. This is a bare index; a slightly fuller one-line-per-
decision summary (including what each amendment changed) lives in
[`docs/roadmap/decisions-log-detail.md`](docs/roadmap/decisions-log-detail.md)
if the doc's own title isn't enough to place it. When a new decision doc is
added, add its one-liner to both places.

1. [`0001`](docs/decisions/0001-blackout-vs-recurring-series.md) — a blackout has absolute priority over a recurring series.
2. [`0002`](docs/decisions/0002-tenant-admin-cancellation.md) — a TenantAdmin can cancel any booking in their tenant. **Amended 2026-09-08**: reach = the read filter, cancel window = `EndsAtUtc`, second cancel refused, self-cancel notifies no one.
3. [`0003`](docs/decisions/0003-availability-timezone.md) — availability is expressed in the resource's timezone, not the booker's.
4. [`0004`](docs/decisions/0004-no-show-definition.md) — no-show = `Confirmed`, never checked in, past the grace period; system-initiated.
5. [`0005`](docs/decisions/0005-capacity-semantics.md) — `Capacity` means concurrent units, not seats. **Amended 2026-09-04**: `Capacity = 1` *is* exclusivity; `ResourceType` never constrains it.
6. [`0006`](docs/decisions/0006-orgid-denormalization.md) — `Bookings.OrgId` duplicates `Resources.OrgId` deliberately, enforced by a same-org FK.
7. [`0007`](docs/decisions/0007-recurrence-materialization-horizon.md) — a series is fully materialized at creation, capped at two years, no top-up job.
8. [`0008`](docs/decisions/0008-dst-spring-forward-policy.md) — a spring-forward occurrence is skipped, not shifted; user told immediately and again by email.
9. [`0009`](docs/decisions/0009-jwt-claims-and-token-lifetimes.md) — JWT claim shape and lifetimes (`sub`/`email`/`orgId`/`role`, 15-min access, 14-day absolute refresh).
10. [`0010`](docs/decisions/0010-global-email-uniqueness.md) — email identifies exactly one user platform-wide; no tenant discriminator at login.
11. [`0011`](docs/decisions/0011-refresh-token-hashing-and-rotation.md) — refresh tokens are SHA-256'd CSPRNG values; reuse of a revoked token kills the whole family. **Amended 2026-09-16**: body-based/localStorage token storage is now a final decision, not a "revisit later" — the owner accepts the XSS-theft risk rather than migrate to an httpOnly cookie.
12. [`0012`](docs/decisions/0012-rbac-enforcement-model.md) — RBAC via four named policies plus a deny-by-default fallback; `TenantMember` excludes SysAdmin.
13. [`0013`](docs/decisions/0013-tenant-isolation-mechanism.md) — structural tenant isolation is validation (`SaveChanges*` throws), not assignment; RLS gets an explicit bypass signal.
14. [`0014`](docs/decisions/0014-child-table-tenant-scoping.md) — `AvailabilityWindows`/`BlackoutPeriods` get their own `OrgId` and all three §4.2 mechanisms.
15. [`0015`](docs/decisions/0015-api-contract-and-pagination.md) — offset pagination, DTO conventions. **Amended 2026-09-01**: `…CommandRequest`/`…QueryRequest` naming; response DTOs are per-endpoint, never shared.
16. [`0016`](docs/decisions/0016-error-contract-and-reason-codes.md) — one error contract (`AppException` → `ErrorKind` → status). **Amended 2026-09-01**: `AppException` abstract, each failure a named `sealed` subclass fixing its own kind+code.
17. [`0017`](docs/decisions/0017-test-fixture-booking-inserts.md) — integration test fixtures may insert `Bookings` via raw SQL, never LINQ/`SaveChanges`. **Amended 2026-09-08**: narrows now that `dbo.CreateBooking` exists.
18. [`0018`](docs/decisions/0018-approver-eligibility.md) — a resource approver must be own-tenant, active, `Approver`/`TenantAdmin`; all ineligibility reasons collapse to one code.
19. [`0019`](docs/decisions/0019-blackout-period-lifecycle.md) — blackout periods get full CRUD; overlaps allowed; `DELETE` is a real, non-idempotent hard delete; cascade is forwards-only.
20. [`0020`](docs/decisions/0020-bookable-interval-semantics.md) — a bookable slot is a free interval carrying `remainingCapacity`. **Amended 2026-09-04**: answered per `quantity` (default 1), cut at walls.
21. [`0021`](docs/decisions/0021-daylight-saving-for-availability-ranges.md) — a DST gap/doubling inside an availability window is absorbed by expanding to the actual elapsed UTC interval.
22. [`0022`](docs/decisions/0022-availability-window-midnight-convention.md) — `ClosesAt = 23:59:59` unconditionally means the following midnight.
23. [`0023`](docs/decisions/0023-booking-concurrency-strategy.md) — `dbo.CreateBooking`/`dbo.ApproveBooking` use `UPDLOCK, HOLDLOCK` key-range locks comparing peak (not summed) concurrent quantity against capacity; 1205 retry is part of the design. Carries the measured concurrency/deadlock evidence — see the doc.
24. [`0024`](docs/decisions/0024-dst-fallback-recurrence-policy.md) — a DST fall-back occurrence resolves to the earlier of its two candidate UTC instants, for both start and end.
25. [`0025`](docs/decisions/0025-recurrence-rule-tenant-scoping.md) — `RecurrenceRules` gets its own `OrgId` and all three §4.2 mechanisms (a gap found building WP-5 Phase 2).
26. [`0026`](docs/decisions/0026-notifications-series-anchor.md) — `CK_Notifications_HasContext` now also accepts `RecurrenceRuleId` alone, for the whole-series-cancel notification.

If a task needs a decision that isn't listed above and isn't in this log,
**stop and ask** rather than picking silently.

---

## 10. How to report back

At the end of every task — every time control returns to me — the exact
report format (`Summary` / `Files` / `Migrations` / `Verification` / `Notes`
sections, plus the rules for what goes in each) lives in the `report-back`
skill (`.claude/skills/report-back/SKILL.md`) rather than being retyped here.
Invoke it before writing your final response whenever any file was touched.

Two rules from it are worth restating because they are easy to skip anyway:
every file touched gets a line, no "and some minor edits" — and don't claim
something builds or passes unless you actually ran it.

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

**Each WP below keeps only its task/AC checklist, status, and the one or two
facts needed to know what's true today.** The full delivery narrative — phase
breakdowns, bugs found while building, manual-verification notes, test
counts, and the reasoning behind each in-phase call — was moved out to
`docs/roadmap/` on 2026-09-16 to keep this file a manageable size; nothing
was deleted. Read the linked file before touching a WP's feature area, the
same way §9 already asks for its decision docs.

Source doc for WP-0/1/2: `docs/Work Packages - Week 1 and 2.docx`.

### WP-0 — Project Setup & Foundations — **Done** (2026-08-20)
Full narrative: [`docs/roadmap/wp0.md`](docs/roadmap/wp0.md).
- [x] Create private Git repo; main + feature-branch workflow — skipped by
      owner's choice.
- [x] README describing the project and how to run it locally.
- [x] Scaffold solution structure: backend (`.NET`) + frontend (Angular).
- [x] Set up SQL Server locally and confirm connectivity.
- [x] `.gitignore`, `.editorconfig`, basic solution conventions.
- [x] `AI-USAGE.md` with headers to fill in throughout.

Acceptance criteria:
- [x] Repo clones and both projects build from a clean checkout.
- [ ] Branch protection / no-direct-to-main convention — documented only,
      no repo yet to enforce it in.
- [x] Database connection confirmed from the backend.

### WP-1 — Data Model & Database — **Done** (2026-08-21)
Full narrative: [`docs/roadmap/wp1.md`](docs/roadmap/wp1.md).
- [x] Model core entities: Tenant, User, Resource, AvailabilityWindow,
      BlackoutPeriod, Booking, RecurrenceRule, ApprovalRequest.
- [x] Define relationships, keys, integrity constraints.
- [x] Decide how tenancy is represented on every ownable entity.
- [x] Plan indexing for availability lookups and overlap checks.
- [x] Produce an ERD; write initial migrations + seed data.

Acceptance criteria:
- [x] ERD exists, presented before any application code.
- [x] Migrations run cleanly and seed a realistic multi-tenant dataset (2
      orgs, 9 users, 4 resources, 20 availability windows, 2 blackout
      periods, 2 recurrence rules; idempotent re-run).
- [x] Model represents a two-year weekly recurring booking without redesign.
- [x] Every ownable entity is unambiguously tied to a tenant.

Notes: seed data originally stopped short of `Bookings`/`ApprovalRequests`
(§4.1 needed `dbo.CreateBooking` first, which didn't exist yet) — WP-4
Phase 3 seeded real bookings through the procedure once it did.

### WP-2 — Backend Skeleton, Auth & Tenancy — **In progress**
Full narrative: [`docs/roadmap/wp2.md`](docs/roadmap/wp2.md).
- [ ] Layered architecture: API/application/domain/infrastructure — the
      four projects exist since WP-0; open question for the mentor whether
      this item means "structure exists" or "every layer carries its
      intended responsibilities" (not until write paths land).
- [x] Wire EF Core to the WP-1 schema — `EnableRetryOnFailure` (1205
      included); future transactional code must use
      `CreateExecutionStrategy().ExecuteAsync(...)` per §5.
- [x] Credential login issuing access token + rotating refresh token —
      `POST /auth/login`, decision `0009`'s claim shape and lifetimes.
- [x] Refresh-token rotation and reuse detection — decision `0011`,
      family-scoped revocation.
- [x] RBAC (SysAdmin, TenantAdmin, Approver, Member) — decision `0012`.
- [x] Structural tenant isolation (§4.2) — all three mechanisms landed;
      257 tests including cross-tenant checks (the `Bookings` gap closed
      later, in WP-4 Phase 3).
- [x] Serilog structured logging, correlation ID per request.
- [x] Global exception handler → `ProblemDetails` with correlation ID.
- [ ] Map domain/validation errors to clean, consistent problem responses —
      validation and auth errors done; booking-rejection reason codes were
      still deferred at this point (no write path existed yet).
- [x] Hand-written mediator (no MediatR) with a logging+validation
      pipeline. Gotcha: a validator is only discovered if it's typed
      against the exact concrete request type — nothing checks this at
      startup.

Acceptance criteria: see the source doc. **As of 2026-08-27, all WP-2
acceptance criteria are met** — 257 tests (192 unit, 65 integration).

### WP-3 — Resources & Availability API — **Done** (2026-09-03)
Source doc: `docs/Work Packages - Week 3.pdf`. Plan: `docs/wp3-plan.md`.
Full narrative (all 5 phases + the 2026-09-04 corrections):
[`docs/roadmap/wp3.md`](docs/roadmap/wp3.md).

- [x] CRUD for resources (type, capacity, timezone, description), TenantAdmin
      only. FR-3.1, FR-3.5. Done 2026-08-31.
- [x] Manage availability windows per resource. FR-3.2. Done 2026-09-01 —
      `PUT /resources/{id}/availability-windows`, replace-the-set.
- [x] Manage blackout periods; ensure they override availability. FR-3.4.
      Done 2026-09-02 — full CRUD, decision `0019`, cancellation cascade.
- [x] Mark resources `RequiresApproval` and assign approvers. FR-3.3. Done
      2026-09-02 — `PUT /resources/{id}/approvers`, decision `0018`.
- [x] Build an availability query returning bookable slots. Done
      2026-09-03 — decisions `0020`/`0021`, pure functions in
      `BookSpace.Domain/Availability/`.
- [x] Design clean DTOs, error contracts, and pagination — decisions
      `0015`/`0016`, complete as of 2026-09-03.

Acceptance criteria (source doc):
- [x] An admin can publish a resource with availability and blackout rules.
- [x] The availability query correctly excludes blackout periods and
      existing bookings.
- [x] Non-admins cannot create or edit resources.
- [x] API returns clear, structured errors — every WP-3 reason code has a
      thrower and a status-code assertion.

Four up-front decisions (D1–D4) are all promoted to numbered records: D1 →
`0014`, D4 → `0017`, D2 → `0020`, D3 → `0021`.

**Corrections after WP-3 closed (2026-09-04)** — not a new WP: `ResourceType`
became a filterable enum; the duration-limit check split into
`CanFitABooking`/`AllowsBookingDuration`; the availability query gained a
`quantity` parameter (fixing the bug `0020`'s amendment records). 666 unit +
279 integration tests pass.

### Future work packages
Appended here as the mentor sends them — one subsection per WP, same
checklist format as above, status kept current as work lands.

### WP-4 — Core Booking Engine — **Done** (2026-09-08)
Source doc: `docs/Work Packages - Week 4.pdf`. Plan: `docs/wp4-plan.md`.
Mentor-facing defense doc: [`docs/wp4-defense.md`](docs/wp4-defense.md).
Full narrative: [`docs/roadmap/wp4.md`](docs/roadmap/wp4.md).

Tasks — Week 3 (correct for a single user):
- [x] Create a one-off booking for an available slot. FR-4.1.
- [x] Reject bookings outside availability, inside blackout, or over
      capacity. FR-4.3.
- [x] Return a clear, machine-readable reason on rejection. FR-4.5.
- [x] Let a member view and cancel their own bookings. FR-4.4 — decision
      `0002`.
- [x] Tests for the happy path and each rejection reason.

Tasks — Week 4 (correct under concurrency):
- [x] Test that fires two bookings for the same slot simultaneously.
- [x] Concurrency strategy making double-booking impossible. FR-4.2 —
      `dbo.CreateBooking`, `UPDLOCK, HOLDLOCK`, decision `0023`.
- [x] Concurrent test proven at both the procedure and HTTP levels.
- [x] Strategy documented and defended — decision `0023`.

Acceptance criteria: all four met (AC-1 exactly-one-succeeds; all rule
violations rejected with clear reasons; cancel frees the slot; strategy
documented and defended).

Notes: cancelling is a plain EF write (not a §4.1 stored-procedure path) —
`Booking.CanBeCancelled` tests `EndsAtUtc`, and `RowVersion` handles
concurrent cancels. §4.1's original "sum" wording was corrected to "peak"
during this WP's planning. A cancelled `Pending` booking keeps its
`ApprovalRequests` row `Pending` — flagged for WP-5 rather than fixed here.

### WP-5 — Recurrence, Approvals & Time Correctness — **Done** (2026-09-09)
Source doc: `docs/Work Packages - Week 4.pdf`. Plan: `docs/wp5-plan.md`.
Full narrative: [`docs/roadmap/wp5.md`](docs/roadmap/wp5.md).

- [x] Create recurring bookings (daily/weekly/monthly) with interval and end
      condition. FR-5.1 — `POST /recurrence-rules` (Phase 1).
- [x] Make each occurrence independently viewable and cancellable. FR-5.2
      (Phase 2 — free from Phase 1's `RecurrenceRuleId` anchoring).
- [x] Support cancelling one occurrence or the whole remaining series.
      FR-5.3 — `POST /recurrence-rules/{id}/cancel` (Phase 2).
- [x] Surface collisions/blackout conflicts at creation — never drop them
      silently. FR-5.4.
- [x] Implement the approval workflow, re-checking availability at approval
      time. FR-7.1–FR-7.5 (Phase 3) — `dbo.ApproveBooking` inherits decision
      `0023`'s lock design whole.
- [x] Store all times as UTC; render in the correct local zone. FR-6.1 —
      already true by construction since WP-1.
- [x] Define and implement DST-transition behavior for recurring bookings.
      FR-6.2 — spring-forward skips (`0008`), fall-back resolves to the
      earlier instant (`0024`, §9's last open item, now closed).

Acceptance criteria: all four met — series create/cancel; conflicts surfaced
at creation; approval re-checks availability (AC-5); the DST edge case
resolves per policy with no crash or duplicate (AC-3).

Notes: found and fixed decisions `0025` (`RecurrenceRules` had no tenant
isolation at all) and `0026` (`Notifications` series anchor) while building
Phase 2. Final baseline: 1039 unit + 468 integration tests.

### WP-6 — Angular Foundation & Auth — **Done** (2026-09-14)
Source doc: `docs/Work Packages - Week 5 and 6.pdf`. Plan:
`docs/wp6-plan.md`. Built as a teaching exercise — the owner is new to
Angular/frontend generally. Full narrative:
[`docs/roadmap/wp6.md`](docs/roadmap/wp6.md).

- [x] Set up the Angular app with standalone components and sensible
      routing.
- [x] Build login; store and refresh tokens correctly on the client.
- [x] Add an HTTP interceptor that attaches auth and handles token refresh.
- [x] Add route guards so unauthenticated users can't reach protected pages.
- [x] Establish a state-management approach (signals + plain injectable
      services, no state library) and stick to it.
- [x] Handle API errors gracefully in the UI.

Acceptance criteria — all four met, verified against the real running
backend: login reaches an authenticated area; protected routes are
inaccessible without a valid session; token refresh is transparent; API
errors surface as clear feedback.

Notes: this app is **zoneless** — a template only reacts to a signal write
or an Angular-recognized event, never a plain field mutated after an
`await`. Worth remembering for every WP-7 component. 26 vitest tests pass.

### WP-7 — Booking UI & Calendar — **In progress**
Source: `docs/Work Packages - Week 5 and 6.pdf`. Plan:
`docs/wp7-plan.md`, approved 2026-09-15. The owner has allocated more than a
week to this package and asked for every phase to be built seriously and
split into its own reviewable steps. Full narrative:
[`docs/roadmap/wp7.md`](docs/roadmap/wp7.md).

- [x] Resource list and detail views. Done 2026-09-15 (Phase 1).
- [x] Availability view for a resource and date range. Done 2026-09-16
      (Phase 2).
- [x] Booking form for one-off and recurring bookings, with clear
      validation feedback. **Done 2026-09-17** (Phase 3).
- [x] Calendar view rendering bookings, including recurring series, without
      choking on volume. **Done 2026-09-18** (Phase 4) — the hard problem.
- [ ] Approval queue UI for approvers. **Phase 6, the last unbuilt screen.**
- [x] Cancellation and blackout handling in the UI. **Done 2026-09-18**
      (Phase 4) — occurrence and whole-series cancellation, and the three
      cancellation readings including a blackout's null actor.
- [ ] Wire the full flow end-to-end against the real API. **Phase 7.** The
      whole path is verified at the API level; the browser click-through is
      what remains.

Acceptance criteria (two met, two open):
- [ ] A member completes browse → book → confirm entirely through the UI.
      Every screen on that path exists, and as of 2026-09-18 the **whole path
      is verified end to end against the running API** (browse → availability
      → book → calendar → detail → cancel → calendar). What is missing is the
      click-through itself, which no tool here can perform. Phase 7's sweep
      is where it gets ticked.
- [x] Recurring bookings render correctly in the calendar. **Done 2026-09-18** —
      occurrences carry a recurrence marker; verified against a real series.
- [x] The calendar stays responsive under realistic data volume. **Done
      2026-09-18**, and measured rather than asserted: DOM is bounded by the
      chip cap, not by the data — 50 → 1000 bookings holds at 56 chips. A
      browser-level measurement has not been taken (jsdom figures only).
- [ ] An approver can action pending requests from the UI. **Phase 6.**

Notes: Phase 1 deliberately does not render the admin resource CRUD actions
the provided designs show — flagged rather than silently dropped. No
browser-automation tool was available to click through Phase 1's own
walkthrough — flagged as a verification gap; Phase 2's flow, by contrast,
was clicked through live by the owner directly and confirmed working.
`resources/:id/book` is the real booking screen since Phase 3. Phase 2's
"Continue to booking" navigates there carrying the selected UTC span and
quantity — router state until 2026-09-17, when the owner had it moved to
**query parameters** (`?startUtc=…&endUtc=…&quantity=…`) so a chosen slot is
shareable, bookmarkable and visible; `features/booking/booking-arrival.ts`
owns both halves of that contract, and `?mode=recurring` besides.

Phase 3 (2026-09-17) delivered both halves of the booking form in eight
steps, across two branches at the owner's own seam: one-off (steps 1–5) and
recurring (steps 6–8). Three owner decisions reshaped it mid-build — query
params over router state, editable recurring times guarded by a client-side
availability-window check rather than locked, and `?mode=recurring` as a
direct entry point so the availability screen is no longer a toll booth on
the way to a series. Two bugs were found by the owner clicking, neither by
the suite (a `<select [value]>` binding showing the wrong time; a hand-edited
`?quantity=16` silently booking one unit) — the lesson, recorded in
`docs/roadmap/wp7.md`: for anything the user sees, assert against the
rendered DOM and prove the regression test fails against the old code.

**Recurring-booking hardening pass, 2026-09-17** — not a new phase; seven
findings reviewed against the code, six fixed, one answered with a decision.
Full narrative in [`docs/roadmap/wp7.md`](docs/roadmap/wp7.md). What is true
now, in case it matters before touching this feature area:

- `validateRecurrenceForm` validates structural primitives **before** any
  derived date arithmetic, and uses `Number.isSafeInteger`. Both are
  load-bearing: `local-date.ts` is `Date` arithmetic underneath and throws
  `RangeError` on a cleared date box or an out-of-range count, from inside a
  `computed` the template reads.
- `POST /recurrence-rules` failures go through `recurrence-rejection.ts`, not
  the one-off map — `booking-rejection.ts` now holds the shared machinery and
  a `RejectionDialect` per endpoint. A recurrence failure never suggests
  going back to availability: a series has no picked slot.
- Every control feeding a submit is disabled while it is in flight, and the
  series outcome panel renders from a snapshot of what was submitted.
- A recurring "safe retry" is offered only while the form still builds
  byte-identical bytes to the pending attempt. The idempotency key is
  **deliberately not persisted** — same-page only, said so on screen.
- Series wording follows `requiresApproval`: "Series submitted" / "Pending
  approval" where every occurrence is created `Pending` (FR-7.1).
- `recurrenceUnavailableReason` is an explicit state for a resource with no
  bookable hours configured. It is **not** "fully booked" — that stays the
  server's answer, reported per occurrence (FR-5.4).

**Phase 4 re-planned, 2026-09-18 — My Bookings cancelled, Phases 4 and 5
merged.** Owner's call, taken after Phase 4's step 1 had already shipped. Full
reasoning in [`docs/wp7-plan.md`](docs/wp7-plan.md)'s Phase 4 preamble; the
narrative is in [`docs/roadmap/wp7.md`](docs/roadmap/wp7.md). What is true now,
before touching this area:

- **There is no My Bookings screen and there will not be one.** A separate list
  is redundant once a calendar exists, and the source PDF never asked for one —
  it asks for a calendar and for cancellation handling, both of which Phase 4
  now carries. `/my-bookings` is **removed**, not repointed.
- **The calendar is the landing screen**, at `/calendar`, with `/home`
  redirecting to it. Home had been a placeholder since WP-6 with no job
  assigned to it anywhere in the PRD or any work package, so nothing was
  displaced. The nav item is "Calendar"; "My Bookings" is deleted.
- **Phase 5's number is retired, not reused.** Phases 6 and 7 keep theirs, so
  every existing reference to "Phase 5, the hard problem" still resolves.
- **Which statuses the calendar draws is a rendering rule, not a query.**
  `ListBookingsQueryRequest.Status` takes one value, not a set, so "everything
  except cancelled" cannot be asked for server-side: the bounded window is
  fetched unfiltered and filtered in the client. Drawn — `Confirmed`,
  `Pending` (distinctly, and by more than colour), `Completed`/`NoShow` muted.
  Not drawn — `Cancelled` and `Rejected`; both hold no time, and
  `NotificationKind` covers telling the member by email.
- **The accepted cost, recorded rather than discovered later**: a cancelled
  booking's *reason* — including decision `0019`'s blackout snapshot — is
  readable only by direct link to `/bookings/:id`. Owner accepted this on
  2026-09-18.
- **Step 1 shipped before the re-plan and survived it untouched.**
  `BookingsService.list()/getById()/cancel()` and
  `RecurrenceRulesService.cancel()` exist and are verified against the live API;
  `list()` is exactly the date-window-bounded fetch the calendar needs.
- **Step 2 is done (2026-09-18)**: `features/calendar/` — `calendar-range.ts`
  (URL contract, week/month boundaries, the UTC fetch window, day-cell layout,
  the week hour axis) plus the component, built against both provided designs.
  The fetch is bounded to the visible window and **walked across pages**
  (`MaxPageSize` is 100 and rejects anything larger rather than clamping); a
  window is never "everything, filtered in the browser", and **there is no
  client-side recurrence expansion anywhere in this feature** — decision `0007`
  already made every occurrence its own row. 754 vitest tests.
- **Step 4 is done (2026-09-18)**: `features/booking/detail/` on
  `/bookings/:id`, reached by clicking a chip. It **must keep rendering a
  cancelled booking correctly** even though the calendar no longer draws one —
  a direct link, a bookmark and the booking screen's "check your calendar"
  message all still resolve there, and that is when someone most wants to know
  what happened. The three cancellation readings (self / administrator / null
  actor = blackout) are the point of the screen, not decoration.
- **Steps 5–6 (cancelling) are done (2026-09-18).** Two facts worth holding on
  to before touching that area: the occurrence rule and the series rule are
  **different** — `Booking.CanBeCancelled` is not-terminal **and**
  `EndsAtUtc > now`, while `RecurrenceRule.CanBeCancelled()` is `Status ==
  Active` with **no time component** — so a live series stays cancellable from a
  past or already-cancelled occurrence. And **the client cannot check the second
  rule at all**: no response carries the rule's status and there is no
  `GET /recurrence-rules/{id}`, so the action is offered optimistically and the
  422 explains it. A read endpoint would close that; it belongs to a future
  backend package alongside `POST /bookings`' missing idempotency key.
- **Neither cancel is idempotent, so nothing on either path ever offers a
  retry** — a repeat rewrites who called the meeting off. The only action on
  failure is "Reload this booking".
- **The week view makes two assumptions that are easy to undo by accident.**
  Overlapping bookings are packed into side-by-side columns (`layOutDay`) rather
  than every chip spanning the width — a member with two bookings at once is
  ordinary, and full-width chips painted over each other. And a booking under 45
  minutes gets a one-line chip (`isCompactChip`): the chip is `overflow: hidden`
  and a row is a fixed 56px per hour, so the two-line shape silently swallowed
  the names of short bookings.
- **The week grid's geometry has one source and must keep it**:
  `minuteOffsetPercent` is the only place a time becomes a vertical position,
  and both the hour labels and the chips resolve through it. The grid draws one
  row per hour *span* (`hourRows()`), deliberately one fewer than the labels
  (`hourTicks()`, which marks both ends). Giving labels a grid row each is what
  made every chip sit an hour-fraction low on 2026-09-18 — and no percentage
  assertion can catch that, since jsdom does no layout and the emitted string is
  identical either way.
- **Test gotcha this step surfaced, worth knowing before editing route specs**:
  a spec that navigates to `/home` now lands on the calendar, which fetches
  immediately — leaving an open request whose `httpMock.verify()` failure
  **corrupts the shared TestBed for every spec file after it**, showing up as
  unrelated failures that vary run to run. Route specs use placeholder routes
  (`/settings`, `/help`) unless they are specifically about the calendar.
- **Timezone discipline in frontend tests**: the calendar reads instants in the
  viewer's zone, CI runs in UTC and local development here is CET, so specs
  build instants from local components (`new Date(2026, 8, 24, 9, 0)`) rather
  than from `"...Z"` literals, which would be silently environment-dependent.

**Phase 6 planned 2026-09-21** (six steps, in [`docs/wp7-plan.md`](docs/wp7-plan.md);
narrative in [`docs/roadmap/wp7.md`](docs/roadmap/wp7.md)). Two things are true
before touching this area:

- **The queue is built without waiting for a design**, owner's call, on the
  app's existing card vocabulary — the route booking detail and cancel took.
- **`GET /bookings` now returns `createdAtUtc` on each row** (step 1, done
  2026-09-21). This is a deliberate owner override of wp7-plan.md §7's "a
  frontend package does not patch the backend" rule, not drift. The gap it
  closes: `BookingSortFields` whitelists `createdAtUtc`, so the endpoint would
  *order* by a field it would not *return*, and Phase 6's requested-at column —
  "how long has this been waiting" — had nothing to render. No migration; the
  column exists and the detail read already projected it. The other backend gap
  this package raised, `POST /bookings`' missing idempotency key, was answered
  the other way and still belongs to a future package — the stop-and-ask is the
  rule, the answer is the owner's, and the two answers differ on cost.

### Hardening pass — 2026-09-15
Not a work package: a response to an external code review (15 items across
booking concurrency, recurrence idempotency, the frontend auth stack, CI and
accessibility), each verified against the actual implementation before
anything was changed. The verification pass itself **found four real,
previously undiscovered bugs**, three inside an already-merged but
undocumented earlier fix (commit `6f15e7d`, 2026-09-11, zero test coverage).
Final baseline: 1066 unit + 497 integration, 0 failed; 49 Vitest tests.
Full narrative: [`docs/roadmap/hardening-pass-2026-09-15.md`](docs/roadmap/hardening-pass-2026-09-15.md).

Key outcomes: interceptor bearer-token scoping fixed; `Resources` gained its
own `RowVersion` (decision `0023` amendment); `dbo.CreateBooking` and
`dbo.ApproveBooking` gained `HOLDLOCK` reads closing two TOCTOU gaps;
cross-tab refresh coordination and a logout-vs-refresh race fixed; expired
and transient refresh-failure handling fixed; recurring-series creation made
crash-resumable via a new `RecurrenceCreationOperation` + idempotency key
(this itself surfaced three more bugs); recurrence validator bounds
corrected; frontend CI added; strict TS/Angular flags turned on; toast/form
accessibility fixes.

**Process note, kept because it's a lesson for how this file gets
maintained**: this pass's first draft was produced by an AI assistant
explicitly instructed only to research, not to write code — it wrote the
implementation anyway, unsupervised, across all 15 items. The owner caught
this before review and required a full adversarial audit of the diff rather
than a discard-and-restart; that audit is what found the bugs above, none of
which the original review or the unsupervised draft had surfaced.

### Resource list filters extended for WP-7 — 2026-09-15
Not a work package: `GET /resources` gained `search` and `requiresApproval`
query parameters (decided and built properly after the owner reversed an
earlier call to filter client-side). No new reason code. 10 new integration
tests; baseline 1066 unit + 507 integration. The WP-7 Phase 1 frontend needed
redoing against these real parameters, including a ~500ms search debounce —
tracked in `docs/wp7-plan.md` rather than here, since it wasn't shipped yet
at the time. Full detail:
[`docs/roadmap/resource-list-filters-2026-09-15.md`](docs/roadmap/resource-list-filters-2026-09-15.md).

### Frontend hardening pass — 2026-09-16
Not a work package: a focused pass over the WP-7 Phase 1/2 frontend
(resource list/detail, availability) against 13 numbered findings, each
verified against the actual code before anything changed. Ten items were
confirmed and fixed; one (auth token storage moving off localStorage into an
httpOnly cookie) needs a real backend contract change and was deliberately
left as a plan rather than a half-migration; one (splitting
`AvailabilityComponent`) was deferred as a follow-up rather than bundled
into a pass already landing this many functional changes to the same file.
Baseline: 243 Vitest tests (was 218), production build clean.

Key outcomes:
- `auth.interceptor.ts` now parses both sides with `URL` and compares
  `origin` (plus a segment-bounded base-path check) instead of
  `startsWith`/`includes` — the old check let a same-text-prefix lookalike
  origin (`http://localhost:52700`, `http://localhost:5270.evil.com`) pass.
- Cross-tab refresh coordination now prefers the Web Locks API
  (`navigator.locks`, a true mutex) over the best-effort localStorage lock
  from the 2026-09-15 pass, which stays as the fallback for a browser
  without it. `AuthService.performRefreshIfNeeded` double-checks the
  refresh token against what was current when the call started, so a
  lock-queued caller that finds a peer already rotated it adopts the result
  instead of risking decisions/0011's reuse-detection.
- The shell route (`app.routes.ts`) gained `canActivateChild`, not just
  `canActivate` — session expiry is now caught on navigation between
  already-loaded shell children (home -> settings, etc.), not only on first
  entry.
- `ResourceDetailComponent` and `AvailabilityComponent` now drive their
  route-id-keyed resource fetch through a `switchMap` pipeline (merged with
  a `retry$`/`retryResource$` Subject) instead of a manual subscribe — a
  stale fetch is cancelled outright rather than merely ignored by an ID
  check, closing a real gap `ResourceDetailComponent` had no guard for at
  all.
- The availability grid's local-selection -> UTC conversion
  (`resourceLocalMinutesToUtc`, `availability-grid.ts`) is DST-correct — it
  no longer assumes local and UTC minutes move in lockstep, which drifted
  by the transition's own delta (not always 60 minutes — Lord Howe's is 30)
  whenever one fell inside the selected segment. `DaySegment`'s own
  boundary construction (`splitIntervalByLocalDay`) still uses the old flat
  offset for the narrower case of an overnight-spanning window whose
  midnight crossing lands on a transition night — noted in that function's
  own comment, not silently left.
- `effectiveMinDuration`'s 15-minute UI step no longer doubles as an
  invented minimum-duration business rule: `durationError` now checks
  `resource.minDurationMinutes` directly, so `null` (no configured minimum)
  can never produce a false "requires at least 15 minutes" message.
- The availability grid's empty-day text is "No availability" unless the
  already-loaded `ResourceDetail.availabilityWindows` prove that weekday has
  no window at all, in which case it's "No bookable hours" — an empty
  interval list alone no longer implies "closed" (it can just as easily mean
  fully booked, blacked out, or insufficient pooled capacity).
- `GET /resources` real pagination (Previous/Next, using the
  `PagedResult.totalPages`/`hasPreviousPage`/`hasNextPage` fields the
  backend already returned) replaces the old "showing 100 of N — narrow by
  type" truncation notice. Any filter change resets to page 1.
- Archived resources no longer show an active booking CTA ("Book resource"
  on the list, "Check availability" on detail) — both render a plain
  "Archived — not bookable" notice in the same slot instead.
- Accessibility: the resource-type filter is a `role="group"` of plain
  `aria-pressed` buttons, not a fake `tablist`/`tab`; the resource card's
  "view details" is a real `<a>` (`.card-title-link`), not a `role="link"`
  div wrapping a real anchor; availability segment buttons carry
  `aria-pressed` and a spelled-out `aria-label` ("Tuesday, Sep 22, 10:00 to
  12:00, 3 units remaining"); the drag handles/overlay are `aria-hidden`,
  with the pre-existing Start/End `<select>`s as the real keyboard/
  screen-reader path for the same narrowing.

Decided, not deferred: refresh-token storage (item 12 of the review) stays
body-based/localStorage-held permanently — the owner reviewed the tradeoff
(moving to an httpOnly cookie needs `AllowCredentials`, `Set-Cookie` on
three backend endpoints, and a CSRF story that doesn't exist today, since
there's no antiforgery middleware anywhere in `backend/src`) and chose to
accept the risk of a stolen refresh token via XSS rather than do the
migration. See decision `0011`'s own final amendment (2026-09-16) — this is
now closed, not an open pre-production task to re-raise later.
`AvailabilityComponent`'s size (766 lines before this pass) was left
unsplit — this pass alone added a DST fix, a duration-validation fix, an
empty-state fix, and accessibility changes to that same file; extracting
child components in the same pass would have compounded the regression
risk without a matching increase in test coverage.
