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
- [`user-management-plan.md`](docs/user-management-plan.md) — **the next
  package, planned and not started.** Read it before touching `Users`,
  `UsersController` or anything on the auth surface: it records four owner
  decisions, the PRD gap it is built on, and why FR-2.4 turns out to be
  satisfied already.
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

Users (user management phase 3): `EmailAlreadyInUse` — `Conflict`, and one code
with one message whether the address is in the caller's tenant or another
(decision `0010` + AC-4). It is raised by `UQ_Users_Email` firing on the insert,
not by a pre-check, so the path that reports it never learns which.

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
27. [`0027`](docs/decisions/0027-approver-booking-detail-reach.md) — an Approver may read a booking by id when it is **their own or** on a resource they gate; the union, not the list's intersection.
28. [`0028`](docs/decisions/0028-approval-gating-without-approvers.md) — a resource may require approval with **no approvers assigned**; FR-3.3's implies-approvers invariant is removed, `ApproversRequired` is deleted, and the request falls back to the tenant's admins.

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

### WP-7 — Booking UI & Calendar — **Done** (2026-09-22)
Source: `docs/Work Packages - Week 5 and 6.pdf`. Plan:
`docs/wp7-plan.md`, approved 2026-09-15. The owner has allocated more than a
week to this package and asked for every phase to be built seriously and
split into its own reviewable steps. Full narrative:
[`docs/roadmap/wp7.md`](docs/roadmap/wp7.md). Click-through script:
[`docs/wp7-clickthrough.md`](docs/wp7-clickthrough.md).

Six live phases (Phase 5's number retired into Phase 4), 861 vitest tests at
close. **All four acceptance criteria met**, the last of them by the owner
walking the click-through on 2026-09-22.

- [x] Resource list and detail views. Done 2026-09-15 (Phase 1).
- [x] Availability view for a resource and date range. Done 2026-09-16
      (Phase 2).
- [x] Booking form for one-off and recurring bookings, with clear
      validation feedback. **Done 2026-09-17** (Phase 3).
- [x] Calendar view rendering bookings, including recurring series, without
      choking on volume. **Done 2026-09-18** (Phase 4) — the hard problem.
- [x] Approval queue UI for approvers. **Done 2026-09-21** (Phase 6) — `/approvals`,
      plus approve/reject on the booking detail screen through the same shared
      panel.
- [x] Cancellation and blackout handling in the UI. **Done 2026-09-18**
      (Phase 4) — occurrence and whole-series cancellation, and the three
      cancellation readings including a blackout's null actor.
- [x] Wire the full flow end-to-end against the real API. **Done 2026-09-22**
      (Phase 7) — a navigation-chain spec that follows only rendered links, the
      coverage sweep, an API-level sweep of all four criteria on one dataset,
      and the owner's own click-through.

Acceptance criteria — **all four met** (2026-09-22):
- [x] A member completes browse → book → confirm entirely through the UI.
      **Met 2026-09-22**, by the owner walking
      [`docs/wp7-clickthrough.md`](docs/wp7-clickthrough.md) path A end to end:
      sign in → calendar → resources → resource → availability → pick a slot →
      book → Confirmed → the chip on the calendar → the booking → cancel.
      Deliberately **not** ticked on the API-level evidence alone, which had
      existed since 2026-09-18: the criterion asks for a member completing it
      *through the UI*, and no amount of request/response evidence converts into
      that claim.
- [x] Recurring bookings render correctly in the calendar. **Done 2026-09-18** —
      occurrences carry a recurrence marker; verified against a real series.
- [x] The calendar stays responsive under realistic data volume. **Done
      2026-09-18**, and measured rather than asserted: DOM is bounded by the
      chip cap, not by the data — 50 → 1000 bookings holds at 56 chips. A
      browser-level measurement has not been taken (jsdom figures only).
- [x] An approver can action pending requests from the UI. **Done 2026-09-21** —
      approve and reject with an optional note, from the queue or the booking.
      The concurrent-decision race was forced live: one 200, one 422
      `BookingNotPending`, rendered as "already been decided".

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

**Phase 6 — approval queue — Done 2026-09-21** (six steps; plan in
[`docs/wp7-plan.md`](docs/wp7-plan.md), narrative in
[`docs/roadmap/wp7.md`](docs/roadmap/wp7.md)). 813 vitest tests. What is true
before touching this area:

- **The queue sends one role-agnostic request**, `GET /bookings?scope=tenant&
  status=Pending&sort=createdAtUtc`, and **must not start branching on role**.
  An Approver and a TenantAdmin send identical bytes; the server narrows the
  rows through `ApprovalReach` (assigned resources / everything). A client-side
  branch would be a second copy of an authorization rule that cannot see what
  the server sees — which resources an approver gates is not in the token.
- **`GET /bookings` now returns `createdAtUtc` on each row** (step 1). A
  deliberate owner override of wp7-plan.md §7's "a frontend package does not
  patch the backend" rule, not drift: `BookingSortFields` already whitelisted
  `createdAtUtc`, so the endpoint would *order* by a field it would not
  *return*, and the requested-at column had nothing to render. No migration.
  The package's other backend gap, `POST /bookings`' missing idempotency key,
  was answered the other way and still belongs to a future package — the
  stop-and-ask is the rule, the answer is the owner's, and the two differ on
  cost.
- **Decision `0027` widened the detail read**, because the queue's own link was
  answering 404: an approver may read a booking that is **their own *or*** on a
  resource they gate. It is a **union**, not the list's intersection — see the
  decision for why reusing `AnyOwnerRestrictedToResources` would have removed an
  approver's access to their own bookings elsewhere. The cancel deliberately did
  not widen.
- **The booking detail screen now serves two audiences**, and `viewerIsOwner`
  is what tells them apart. Before it existed, an approver was told "the time is
  not held for *you* yet" and "*You* cancelled this booking" about someone
  else's request, and was offered a cancel button that answers 404. Any new
  second-person string on that screen needs the same treatment.
- **One `DecisionPanelComponent`, two hosts** (queue row and booking detail), so
  the AC-5 wording, the in-flight rules and the no-retry rule exist once.
  Neither decision is idempotent — a repeat answers `422 BookingNotPending` — so
  **nothing on that path ever offers a retry**. The queue drops a decided row
  from the response; the detail screen re-reads, because a decision changes more
  than the response carries.
- **AC-5's 409 is unreachable through the public API, by design.** A Pending
  booking reserves its units in full (`0005`), and capacity cannot be shrunk
  below existing bookings — so nothing legitimate can strand a pending approval.
  The refusal is proven at `dbo.ApproveBooking` (raw-SQL fixture, WP-5) and in
  the decision panel (vitest against a real 409). Defence in depth, not a gap;
  the UI arm still has to exist.
- **Self-approval is permitted, from both screens.** An approver may decide on
  their own pending request for a resource they gate. The backend always allowed
  it (`ApprovalReach` does not exclude the caller) and the queue always offered
  it; the booking detail screen refused until 2026-09-22 on a rule I invented
  and no FR or decision record asked for. Found by the owner walking the Phase 7
  click-through, and notable because **the suite was asserting the invented rule
  rather than merely missing it** — a green suite proves the code matches the
  tests, which is worth nothing when the test is the invention.

**Phase 7 — end-to-end wiring, tests, AC sweep — Done 2026-09-22** (five steps).
What is true before touching this area:

- **`app/tests/navigation-chain.spec.ts` tests seams, not screens.** Every hop
  follows the `href` the previous screen actually rendered. A version that built
  its own URLs would prove the route config resolves — which `app.routes.spec.ts`
  already does — and would have stayed green while the approval queue linked
  members into a 404. Keep that discipline if you extend it.
- **Its `afterEach` runs `verify()` inside a `try/finally` that always resets the
  TestBed**, and the reason is worth knowing before writing any spec that drives
  real routes: one unflushed request there failed `verify()`, which threw before
  anything reset the module, and **every test in every other spec file** then
  failed with "test module already instantiated". One missing line, 39 failures,
  none in the file at fault.
- **The coverage sweep found five holes where an eyeball audit had found two.**
  Enumerate files, do not scan names — `blackout-periods.service.ts` was missed
  because it sits under `availability/services/` rather than beside its siblings.
- **AC-1 and AC-4 were re-proven through real HTTP**, not just at the procedure
  level: five simultaneous attempts at one slot answered `201 409 409 409 409`,
  and cross-tenant reads answered 404.
- **The click-through is a written artefact, not a claim**
  ([`docs/wp7-clickthrough.md`](docs/wp7-clickthrough.md)). It is shaped by this
  package's bug history — ten deliberate wrong turns get as much space as the
  happy path — and it found a real bug on its first walk. Re-walk it after any
  change to the booking or approval flows.

### Admin console — tenant administration UI — **Done** (2026-09-23)
Plan: [`docs/admin-plan.md`](docs/admin-plan.md). Click-through script:
[`docs/admin-clickthrough.md`](docs/admin-clickthrough.md). **Owner-initiated, not a
mentor work package** — the same standing as the hardening pass below and the
resource-list-filters entry, and deliberately *not* numbered as a WP, because
§12's rule is that this roadmap mirrors the packages the mentor issues rather
than an invented build order.

Raised by the owner after WP-7 closed: the remaining work packages barely touch
the frontend, and **tenant admin CRUD is the largest thing still missing from
the application** — resource create/edit/archive, availability windows,
approvers, and blackout periods. It is not new scope; it has been flagged as a
gap in `docs/wp7-plan.md` §7 and `STATE-OF-THE-APP.md` §4 since WP-7 Phase 1,
with the buttons left absent rather than shown disabled.

**All seven phases are done, and the owner walked
[`docs/admin-clickthrough.md`](docs/admin-clickthrough.md) end to end on
2026-09-23 — paths A to E, everything passing, no defects reported.** That walk
is what closes this, not the suite and not the API probes; the claim is that an
administrator can run their tenant without being misled, and no amount of
request/response evidence converts into it (the same rule WP-7 applied to its
first acceptance criterion). Each phase was built in one go rather than split
into steps (owner's call, 2026-09-22). Three things settled before planning:

- **Scope is resources, windows, approvers and blackouts** — what the backend
  already supports. **User management is out**: inviting, deactivating and
  assigning roles have no backend at all. `UsersController` exists since phase 1
  but is a single eligibility-filtered read — it is not the start of a user
  directory, and nothing should treat it as one.
- **The one backend addition, `GET /users`, is built** (phase 1, 2026-09-22).
  `PUT /resources/{id}/approvers` takes user ids and nothing in the API listed
  users, so an admin could see who was assigned and could not discover who they
  could assign. Tenant-scoped, TenantAdmin-only, paged, filtered to decision
  `0018`'s eligible set. See the "Phase 1" paragraph below before using it — the
  route is deliberately broader than the answer.
- **Archive stays irreversible and the UI exposes it anyway**, behind a hard
  confirmation. There is no unarchive and `ResourcesController` argues in writing
  against adding one.

Two things worth knowing before touching this area, both audited 2026-09-22
rather than assumed:

- **The API forces two different interaction models.** Availability windows and
  approvers are `PUT` replace-the-set; blackout periods are per-row CRUD with a
  real hard delete (decision `0019`). Making the three screens look alike would
  misrepresent one of them.
- **Replace-the-set has no concurrency protection on the wire.** `Resources` has
  a `RowVersion` (decision `0023`'s amendment) but **no Resources DTO carries
  it**, so two admins editing one resource's windows silently last-write-wins.
  Tolerable for a small admin team; closing it is a backend change.

**Phase 1 — `GET /users` — Done 2026-09-22.** 1073 unit + 536 integration tests
(25 new), plus a live probe of the running API. What is true before using it:

- **The route is broader than the answer, deliberately.** `GET /users` returns
  the decision `0018` eligible-approver set — own-tenant, active, `Approver` or
  `TenantAdmin` — not the tenant's users, and there is no parameter that widens
  it. A Member is absent by design, not by a bug. Said so in
  `ListUsersQueryRequest`'s own header, because a route named `/users` that
  answers with a subset is exactly the thing someone later reads as broken.
- **The eligibility rule now lives in SQL, and had to.** It used to run in memory
  in `UserRepository.FindEligibleApproverIdsAsync`, deliberately, to avoid an
  `EF.Property` expression over the private `_roleAssignments` backing field.
  That trade does not survive paging: filtering after `OFFSET`/`FETCH` pages over
  the wrong set and returns a `TotalCount` counting people the write path would
  refuse. It is now one `Expression<Func<User, bool>>` used by **both** repository
  methods — two copies could disagree, and the disagreement would show up as an
  admin being offered somebody `ReplaceApprovers` then rejects, with `0018`
  collapsing every reason into `ApproverNotEligible` so no screen can say why.
- **`UsersController` stacks both policies**, unlike `ResourcesController` where
  the class-level policy is the weaker one so a forgotten attribute can only
  narrow a write. There is no member-facing read here to be weaker for, and
  `TenantAdmin` alone admits SysAdmin by role — who carries no `orgId` claim, so
  the tenant filter would answer them `200` with an empty page. `TenantMember`
  alongside it turns that into the 403 it should be. Verified live.
- **No migration, no new reason code, and no existing contract changed.** The
  only behavioural change outside the new endpoint is that approver eligibility
  is evaluated by SQL Server rather than by C# — same rule, same answers, proven
  by the existing `ApproverEndpointTests` still passing untouched.

**Phase 2 — the admin shell — Done 2026-09-23.** 894 vitest tests (33 new),
production build clean. Two open questions settled, and three facts every later
phase builds on:

- **The console is its own route tree at `/admin`, not an "admin mode" on
  `/resources`.** The two lists answer different questions — the member list is
  "find something to book" and hides archived rows by design (FR-3.5), the admin
  one is "manage the catalogue" and must show them. WP-7's booking detail screen
  is the cautionary tale for the alternative: one screen for two audiences
  needed `viewerIsOwner` threaded through every string and still shipped telling
  an approver "the time is not held for *you* yet" about someone else's request.
  `adminGuard` sits on the `admin` parent, so every screen phases 3–6 add is
  covered without remembering to ask for it.
- **The console follows the app's existing card vocabulary rather than waiting
  for the outstanding design pass**, as the approval queue did. Recorded in
  `docs/admin-plan.md` §7.
- **`AuthService.isTenantAdmin` admits `TenantAdmin` and nothing else, and
  excluding SysAdmin is deliberate.** `AuthorizationPolicies.TenantAdmin` admits
  them *by role*, so a guard written from the policy name would let them in —
  onto a console where every request answers 403, because every admin endpoint
  also requires the `orgId` claim a SysAdmin does not have (decision `0009`,
  PRD §2). The UI matches the effective permission, not the policy name. Same
  rule in `adminGuard` and in the nav, each with its own test.
- **The rejection machinery now lives in `core/http/rejection.ts` and is generic
  over its field-name type.** It was never booking-specific — it reads a
  ProblemDetails and maps a reason code onto copy — and the admin forms need it
  over a different vocabulary of controls. `features/booking/rejection/
  booking-rejection.ts` keeps the one-off dialect and the booking field union,
  and re-exports `RejectionDialect`/`RejectionCopy` already bound to that union,
  so its three sibling dialects are untouched and their specs are the proof the
  move changed no behaviour. **A new feature's dialect imports from `core/http`
  and binds its own field union; it does not widen `BookingFieldName`.**
- **`ConcurrencyConflict` is reachable on `PUT /resources/{id}`** — `Resources`
  has had a `RowVersion` since the 2026-09-15 hardening pass — and the admin
  dialect tells the admin to reload rather than re-send, because re-sending is
  exactly how the other admin's work gets overwritten. This does **not** close
  `docs/admin-plan.md` §4.2, which is about the replace-the-set child
  collections, where no version reaches the wire at all.

`/admin/resources` rendered the placeholder component until phase 3 replaced it
with the real list, exactly as `/approvals` did from WP-6 until WP-7 Phase 6.

**Phase 3 — resources: create, edit, archive — Done 2026-09-23.** 955 vitest
tests (61 new), production build clean, and the whole flow probed against the
running API. What is true before touching this area:

- **`docs/admin-plan.md` §4.3 is settled, and the answer is structural.** A
  resource that does not exist cannot have approvers, and approvers are assigned
  by a *different* endpoint, so `requiresApproval` can only ever be false at
  creation — `POST /resources` with it true answers **422 `ApproversRequired`**,
  verified live. The control is rendered **disabled with the reason beside it**
  rather than hidden, and opens on the edit form once `approvers` is non-empty.
  **Until phase 5 lands, no resource can be made approval-gated through the UI
  at all**, and nothing links to the approvers screen because it does not exist
  yet. A phase boundary, not a gap.
- **One component serves create and edit** (`AdminResourceFormComponent`). Every
  field and every refusal is shared; the differences are a heading, a CTA, and
  two sections that exist only in edit mode. Splitting it would be two copies of
  the field vocabulary, and the first to drift would be the unwatched one.
- **Archive lives on the form, never on a list row**, because it cannot be
  undone and the one thing worth buying is that the admin is looking at the
  resource when they decide. The confirmation says both things that matter: there
  is no way back, **and** existing bookings are not cancelled (`Resource.Archive`
  flips a flag and nothing else). An acknowledgement tick, not type-the-name —
  nothing is deleted, so type-to-confirm would be disproportionate. The form goes
  read-only once archived, because `PUT` then answers 422 `ResourceArchived`.
- **The timezone picker is `Intl.supportedValuesOf('timeZone')`**, since §4.3
  refuses a resolvable-but-non-canonical id. **That list omits `"UTC"`** — it is
  a tz database *link*, not a zone — so it is added explicitly, after verifying
  against the running API that the backend accepts it. `InvalidTimeZone` stays
  handled: the browser's ICU data and the server's can disagree at the edges.
- **`GET /resources` has `includeArchived` and nothing that narrows *to*
  archived**, so the admin list has a toggle and no archived-only view. Filtering
  a fetched page client-side would leave `totalCount` and the page boundaries
  describing a different set than the rows under them.

**Two bugs found while building this, both outside phase 3's own code and both
now covered:**

- **The shell's breadcrumb crashed on a repeated crumb.** `@for` tracked by the
  crumb's own text, and Angular throws NG0955 on a duplicate track key — which
  takes the entire shell down, not just the breadcrumb. Now tracked by `$index`,
  the only honest key for a list of plain strings.
- **Route `data` inherits further than it looks.** Angular's default
  `paramsInheritanceStrategy` ('emptyOnly') copies a parent's `data` onto any
  child with an empty path **or no component**. A componentless `resources`
  grouping route under `admin` therefore inherited `title: 'Admin'` and the
  breadcrumb read "Admin > Admin > Resources". **The admin routes are flat
  siblings for this reason** — `resources`, `resources/new`, `resources/:id`,
  each with its own component. The member-facing `resources` group has the same
  shape and escapes it only because its parent carries no title to inherit; keep
  that in mind before giving any grouping route a title.

**Decision `0028` — approval gating no longer needs approvers — 2026-09-23.**
Owner's call, raised while reviewing phase 3, and it reverses an FR-3.3
invariant this codebase had enforced since WP-3. Full reasoning in
[`docs/decisions/0028-approval-gating-without-approvers.md`](docs/decisions/0028-approval-gating-without-approvers.md).
What is true now:

- **A resource may require approval with an empty approver list**, and the
  create form offers the flag. The old rule produced the state it existed to
  prevent: the flag and the list are set by different endpoints, so the only
  route to a gated resource was create-ungated → assign → flip, leaving it
  published and **freely bookable** throughout.
- **`ReasonCodes.ApproversRequired` and `ApproversRequiredException` are
  deleted**, as `ApprovalRequired` was in WP-4 Phase 1a — nothing can throw
  them, and §6 keeps the catalogue describing what the API can actually return.
  `ResourceWriteRules.EnsureApproversWhenRequired` is gone with them.
- **Clearing an approver list on a gated resource is now allowed.** Previously
  the only way to drop the last approver was to un-gate the resource first,
  which turned a staffing change into a window where anyone could book it.
- **`NotificationsFor` no longer reads the approver list directly**, in either
  creation handler. Both built one `ApprovalRequested` row per approver on the
  written assumption that the list could never be empty; left alone, a gated
  resource with no approvers would have created Pending bookings notifying
  **nobody**, and FR-9.3's expiry job would have decided them unseen. The
  recipients are the approvers when there are any and
  `IUserRepository.FindTenantAdminUserIdsAsync` when there are not — mirroring
  `BookingApprovalReach`, so whoever can decide is who gets told. It is a
  fallback, not an addition: an assigned list wins outright.

### Phase 4 — Availability windows editor — **Done 2026-09-23**
`/admin/resources/:id/availability-windows`, reachable only from the resource
form. FR-3.2, replace-the-set.

- **An "edit everything, save once" form**, because `PUT /resources/{id}/
  availability-windows` replaces the whole set and an omitted window is a
  deleted one. Blackout periods (phase 6) are per-row CRUD and will look
  different on purpose — `docs/admin-plan.md` §4.1.
- **Decision `0022` is a control, not a value.** `ClosesAt = 23:59:59` means the
  *following midnight*, so the editor renders it as an "Until midnight" tick and
  keeps the flag separate from the time — a stored 23:59:59 and a deliberate
  one-second-to-midnight are indistinguishable on the wire and must not be on
  screen. The minute arithmetic reads it as 1440, never 1439.
- **Overlaps are refused client-side before the request goes out**, per row, and
  the rule is the server's restated exactly — **including that adjacency is not
  overlap**: `ClosesAt` is exclusive, so 09:00–12:00 and 12:00–17:00 coexist.
  Verified against the live API, which accepts that pair and answers 409 for a
  genuine overlap. Being stricter than the API it writes to would be a bug.
- **The rules live in `windows/window-editor.ts` as pure functions**, the way
  `recurrence-form.ts` holds the booking form's. The interesting part is
  arithmetic over times, and arithmetic is worth testing without a TestBed.
- **Save is disabled until something changes**, compared over the payload shape
  rather than the rows — a row deleted and re-added identically is not an edit,
  because the endpoint replaces the set.
- **An empty schedule is a real, saveable state** ("closed"), not a gap to fill.
  The backend validator says the same about an empty array: a resource open at
  no time is one taken out of circulation without archiving it.
- **`availability-rejection.ts` is the sixth dialect**, and the only one that
  offers a retry — replace-the-set is idempotent by construction, so sending the
  same schedule twice is harmless and the copy can say so.

**Phase 5 — approvers editor — Done 2026-09-23.** 1044 vitest tests (37 new),
production build clean, whole flow probed live. What is true before touching it:

- **The picker only ever offers eligible people, and that is forced rather than
  polite.** Decision `0018` collapses every ineligibility reason into one code
  because naming the cause would confirm a cross-tenant id exists (AC-4) — so a
  picker that let an admin type an id could only ever answer "no" without saying
  why. `GET /users` (phase 1) finally has the caller it was built for.
- **`GET /resources/{id}` and `GET /users` do not agree, and the screen has to
  reconcile them.** `FindApproverSummariesAsync` does **not** filter by
  `IsActive`, so somebody assigned and later deactivated still comes back on the
  resource read but *not* from `/users`. A picker built the obvious way — render
  the eligible, tick the assigned — would never show them and **the next save
  would silently drop them**; they cannot be kept either, since re-sending the id
  is refused. They are therefore rendered as a separate "no longer able to
  approve" group that says saving removes them. `strandedApprovers` has its own
  tests because it is empty in every healthy tenant and nothing would exercise it
  by accident.
- **"Stranded" is only trusted when the picker is showing everyone** — no search
  term, one page. A searched picker shows a subset, so absence proves nothing.
- **The selection is held as ids in its own signal**, never as flags on the
  option objects, which are replaced wholesale on every search. A tick lost
  because somebody scrolled out of view would be the same silent removal.
- **Decision `0028`'s loose end is closed**: the resource form's "no approvers
  assigned" warning now links here. It deliberately pointed nowhere in phases 3
  and 4, because the screen did not exist.
- **Test gotcha worth knowing before writing anything against this screen**:
  the two reads go through `forkJoin`, which **cancels its remaining sources the
  instant one errors**. A spec that flushes the failing request first leaves the
  sibling cancelled and unflushable ("Cannot flush a cancelled request"). Answer
  the succeeding one first.

**Phase 6 — blackout periods — Done 2026-09-23.** 1094 vitest tests (49 new),
production build clean, the cascade probed end to end against the running API.
The last screen. What is true before touching it:

- **It is the one genuinely per-row CRUD screen, with the one real hard
  delete.** Blackouts overlap freely (`0019`), each is an independent fact, and
  DELETE removes a row. `docs/admin-plan.md` §4.1 is explicit that making it look
  like the replace-the-set editors would misrepresent all three.
- **§4.4 is settled, and better than the question assumed.** The *response*
  already reports exactly what the cascade cancelled
  (`CancelledBookingSummary`), and `GET /bookings`' `from`/`to` are the identical
  overlap predicate `FindBookingsToCancelAsync` uses — so an accurate pre-flight
  preview is obtainable. The screen does **both**: an opt-in preview labelled as
  decided-at-save-time, and the authoritative record from the response. Only the
  first would be a promise it cannot keep; only the second means an admin learns
  what they cancelled afterwards.
- **An admin types in the resource's timezone, not their own** (decision `0003`).
  A blackout is an *instant*, unlike an availability window, so
  `blackouts/blackout-form.ts` owns a two-pass local→UTC inverse correcting
  against `utcToResourceLocal` — the same technique `availability-grid.ts` uses,
  generalized to an arbitrary date. Tested on both sides of a real Warsaw DST
  transition and on the gap/ambiguity cases, which have no unique inverse and
  must not throw. Both zones are shown in the list.
- **`BlackoutPeriodElapsed` is about the *end*, not the start**, so a blackout
  that began this morning and runs through tomorrow is legal — what an admin
  needs when a room floods. It is a **422**, not the 400 its wording suggests;
  confirmed against the running API rather than inferred.
- **Deleting a blackout is not an undo.** `0019`'s cascade is forwards-only, so
  bookings it cancelled stay cancelled and nobody is notified. The delete
  confirmation says so, because nothing else on the screen would correct an admin
  who assumed otherwise.
- **Nothing on this path offers a retry** — the strictest of the eight dialects.
  Repeating a create makes a *second* blackout (`0019` allows overlaps, so
  nothing refuses it) and the first attempt may already have cancelled bookings
  that never come back.

**Phase 7 — wiring, coverage sweep, click-through — Done 2026-09-23.** 1114
vitest tests (20 new), production build clean, the console's contract re-probed
against the running API. No new screens. What is true before touching this area:

- **All three editors' return legs were `<button (click)="goToResource()">` and
  are real anchors now.** A button works when clicked and fails at everything
  else: no ctrl-click, no new tab, and **`navigation-chain.spec.ts` cannot
  follow it**, because that file's whole discipline is reading the `href` the
  previous screen rendered. Nothing in the suite said so — each editor's spec is
  about its component, and no component spec is about where the next screen is.
  The in-flight guard survives as a shape change rather than a class: an anchor
  cannot be disabled, so while a save is in flight the windows and approvers
  editors render a disabled `<button>` instead. The **archived** branches now
  point at the resource too (it exists, read-only); the **not-found** branches
  still go to the list, because there it genuinely does not.
- **The coverage sweep enumerated files rather than scanning names**, which is
  WP-7 Phase 7's lesson, and found two: `rejection/approver-rejection.ts` (the
  only one of the eight dialects without a spec) and `services/users.service.ts`
  (the client for the one endpoint this console owns). `core/http/rejection.ts`
  is deliberately left without one — all nine of its branches are exercised
  through the eight dialects, including a non-`HttpErrorResponse` input and a
  4xx whose body is not a ProblemDetails.
- **`ApproverNotEligible`'s copy is a security property, not a matter of tone**,
  and now has tests saying so. Decision `0018` collapses all three ineligibility
  causes into one code precisely so that naming one cannot confirm a cross-tenant
  id exists (AC-4) — so the message names neither the person nor the reason, and
  says what is both true and useful instead.
- **The click-through has been walked, and stays a live artefact**
  ([`docs/admin-clickthrough.md`](docs/admin-clickthrough.md)), built the same
  way WP-7's was. Five paths, and path E's deliberate wrong turns are where this
  project's bugs have actually lived. **The owner walked A to E on 2026-09-23
  and everything passed** — unlike WP-7's first walk, which found a real bug.
  That is evidence about this console, not about the method: **re-walk it after
  any change to the admin flows**, because these screens have exactly the
  property that produced WP-7's bugs — what determines what you see is not what
  the assertions are about. Three of its items check behaviour that is
  **known and accepted rather than correct** — the silent last-write-wins on the
  replace-the-set editors, the empty "no longer able to approve" group that no
  UI can produce without a user-management backend, and an archived resource
  that stays in the list forever — and each says so on the page, so finding them
  is not mistaken for finding a bug.
- **The numbers in that script are live, not illustrative** (probed 2026-09-23):
  `GET /users` answers Acme with exactly two eligible people and no Member;
  `GET /resources?includeArchived=true` returns six, three archived; an archived
  `PUT` is 422, a genuine window overlap 409, an elapsed blackout 422; Member and
  Approver are 403 on `GET /users` and `POST /resources`, anonymous 401. Two
  rules are already proven *in the seeded data* and the script uses them rather
  than manufacturing cases: the 3D Printer's adjacent `09:00–12:00` /
  `12:00–17:00` windows (adjacency is not overlap) and the Audi A5's Monday
  window closing at `23:59:59` (decision `0022`'s midnight convention).

### User management — provisioning, roles and account status — **In progress** (started 2026-09-23)
Plan: [`docs/user-management-plan.md`](docs/user-management-plan.md).
**Owner-initiated, not a mentor work package** — same standing as the admin
console above and the hardening pass below, and deliberately not numbered as a
WP: WP-8 has not been issued, and §12's rule is that this roadmap mirrors the
packages the mentor sends rather than an invented build order.

Eight phases; **phases 1–3 are done (2026-09-23/24)**. Four questions were put to the owner and answered
before the plan was written, because none was answerable from the PRD or §9.
Read the plan before starting any phase; what follows is only what is needed to
know the shape.

- **The PRD has no functional requirement for user management, and the plan
  says so rather than inventing one.** Checked 2026-09-23: the authority is the
  §2 persona line — a Tenant Administrator "Manage[s] resources, rules,
  **members, roles**" — plus FR-2.4 on suspension. FR-1.5 is a model constraint,
  FR-1.3 is about *tenants* and a different audience, and PRD §13 never raises
  it. **Cite the persona line and FR-2.4; do not write an FR number that does
  not exist.**
- **FR-2.4 is already satisfied, which deletes a whole phase.**
  `LoginCommandRequestHandler` checks `IsActive`; `RefreshTokenCommandRequestHandler`
  checks `IsActive` *and* `OrganizationStatus.Suspended` and **revokes the whole
  token family**. So deactivation needs no change to the auth stack at all —
  only something that writes the flag, which is what does not exist. The
  15-minute access-token tail afterwards is not a gap: FR-2.4 asks for access to
  be lost "immediately on next token refresh", which is exactly what happens.
- **This package builds the first email path in the application**, and
  **deliberately does not use the `Notifications` outbox** — which is what keeps
  the three unbuilt background jobs out of its way (the owner's own question
  when choosing this). `Notification` is a *scheduler*: it carries `SendAtUtc`,
  `Attempts` and `LastError`, no address and no body, and `CK_Notifications_HasContext`
  anchors every row to a booking or a series. An invitation has neither anchor,
  nothing to schedule, and no meaningful idempotency key. It is therefore sent
  **synchronously inside the request**, and the `IEmailSender` this builds is the
  one the jobs will later need.
- **An emailed invitation implies activation.** `POST /auth/activate` plus a
  hashed, single-use, expiring token table are in scope as a consequence of the
  email decision, not as scope creep — without them `POST /users` creates
  somebody who can never sign in. The token is shaped on decision `0011`: store
  SHA-256 of a CSPRNG value, plaintext only in the email. The endpoint is
  anonymous, so it needs the rate limiting `login`/`refresh` already have, and
  **expired / used / never-existed must be indistinguishable** for `0018`'s
  reason.
- **An email collision answers the same way whether the address is in this
  tenant or another** (`EmailAlreadyInUse`). Decision `0010` made email unique
  platform-wide but settled that for *login*; a refusal that distinguishes the
  two cases is a cross-tenant existence oracle, which is what `0018` collapsed
  three approver reasons into one code to avoid (AC-4).
- **The last TenantAdmin cannot be removed or deactivated** (`LastTenantAdmin`),
  and the guard is **checked under a lock** — two admins removing each other
  concurrently both read "there are two" and both pass. A tenant with no admins
  cannot be managed by any API (FR-1.3 has no controller either) and breaks
  decision `0028`: approval requests on a gated resource with no approvers fall
  back to `FindTenantAdminUserIdsAsync`, so they would notify nobody.
- **`GET /users` gets widened rather than duplicated, with today's answer as the
  default.** The directory needs every user; the route currently answers the
  `0018` eligible set. An omitted parameter must keep today's behaviour
  byte-identical, so a forgotten one narrows rather than widens — the safe
  direction, and the same shape as `includeArchived` on `GET /resources`.
- **Out of scope, deliberately**: self-service change password, re-issuing an
  invitation, SysAdmin tenant management (FR-1.3), and deleting a user (§4.5 —
  nothing is deleted). The second of those is the one the plan pushes back on:
  with no resend, a failed invitation email leaves that person with no route in
  at all, which is why the create response carries the activation link whether
  the send succeeded or not.
- **Three decision records will come out of it** — `0029` provisioning and
  invitation delivery, `0030` email collision disclosure, `0031` the
  last-TenantAdmin guard — each added to §9 here and to
  `docs/roadmap/decisions-log-detail.md`.

**Phase 1 — `IEmailSender`, configuration, development sink — Done 2026-09-23.**
1140 unit + 539 integration tests (69 + 3 new), build clean, all four
configuration outcomes probed against the real host. No endpoint, no migration,
no schema change, no new reason code. Full detail in the plan; what matters
before using it:

- **A delivery failure is a return value, not an exception.**
  `IEmailSender.SendAsync` answers `EmailSendResult`, and both senders catch
  everything — transport, an unwritable directory, a recipient MimeKit refuses.
  That is plan §4.3 made structural: phase 3 has to discard the failure on
  purpose rather than remember a try/catch. The caller's own cancellation is the
  one thing that still propagates.
- **MailKit over any provider's SMTP relay**, not a vendor SDK — there is no
  provider account yet, and picking one now would be a dependency taken before
  the decision it depends on. One new package.
- **`EmailOptions.DeliveryMode` defaults to `Smtp`, deliberately**, so an
  omitted or misspelled `Email` section fails the boot; a `DevelopmentSink`
  default would have written production invitations to a directory nobody reads.
  Which sender is registered is decided once in `AddInfrastructure`, and needs a
  restart to change.
- **A credential over an unencrypted transport is refused at boot** (§4.4),
  along with a missing host, a half-configured credential and an unusable From
  address — all in `EmailOptionsValidator`, which holds every rule rather than
  splitting them across data annotations it cannot express.
- **The sink is not a test double** — it is how this runs locally. It writes an
  `.eml` through the same `MimeMessageFactory` the SMTP sender uses, so what is
  on disk is what would have gone on the wire.
  `backend/src/BookSpace.Api/sent-emails/` is gitignored: every file is a live
  activation link. The log line carries the subject and the path and **nothing
  else** — not the body, which holds a token, and not the recipient, which is a
  personal record.
- **MimeKit is more permissive than a usable-address check**, measured not
  assumed: `TryParse("not-an-address")` is true, and `new MailboxAddress(n, "")`
  succeeds and yields an empty `To`. `EmailAddressRules` is the one rule both
  the startup validator and `MimeMessageFactory` apply.
- **The sink's file name deliberately carries nothing from the message.** It
  carried the recipient first, and since the log line carries the path, that
  made the §4.4 rule above false as written — caught by this class's own test.
  A timestamp and random bytes beat a sanitizer: there is no path-traversal case
  left to get wrong.

**Phase 2 — activation tokens, `User.SetPassword`, `POST /auth/activate` — Done
2026-09-24.** 1194 unit + 555 integration tests (54 + 16 new), one migration
(`AddActivationTokens`), the whole flow probed against the running API. What
matters before touching the auth surface again:

- **`ActivationTokens` is `RefreshTokens`' shape, including its absences** — no
  `OrgId`, no query filter, no RLS predicate. Activation runs before the user
  has ever signed in, so §4.2 has nothing to act on and the token's own secrecy
  is the access control: 256 bits of CSPRNG, SHA-256 stored, single use,
  absolute expiry (decision `0011`'s shape, reused).
- **`SecureToken` (Infrastructure/Security) now holds "a bearer secret this
  application hands out" once**, and both token factories delegate. Two copies
  of a crypto rule is where drift is most expensive.
- **Single use is enforced by a concurrency token**, `ConsumedAtUtc`, exactly as
  `RefreshTokens.RevokedAtUtc` works — so two simultaneous redemptions do not
  both set a password. Model metadata, no DDL.
- **Every refusal is byte-identical**: expired, redeemed, unknown, user
  deactivated, org suspended → `401 InvalidActivationToken` (the sixth code in
  `AuthenticationFailureReason`, which is where auth codes live per §6). The
  password policy is validated *before* the token lookup, so a 400 cannot be
  used to probe whether a token is live, and a rejected password leaves the
  token spendable.
- **`PasswordPolicy` — 12 to 128 characters, no composition rules — is invented
  here and flagged as such.** This is the first place the application sets a
  password; FR-2.3 says hashed and nothing says how long. Length-only follows
  NIST SP 800-63B; the maximum bounds PBKDF2 work on an anonymous endpoint.
- **Activation returns 204, not a session.** Minting one would duplicate login's
  FR-2.4 account-state gate or skip it.
- **The auth repository gained a *write* exemption, and mechanism 2 now honours
  the bypass.** Activation writes to `Users` from an anonymous request, which
  §4.2 refuses twice over: RLS's *filter* predicate hides the row so the UPDATE
  matches nothing (hence
  `IAuthenticationUserRepository.SaveChangesUnfilteredAsync`), and
  `ValidateTenantOwnership` threw when a browser attached an existing session's
  bearer token to the anonymous call. The guard now returns early inside a
  `TenantBypassScope` — the same signal RLS already honours — so the two
  mechanisms agree. Nothing widens: the scope is internal and only
  explicitly-named repository methods may enter it. **Both changes were proven
  load-bearing by removing them** — 8 of 16 new integration tests fail without
  the first, exactly 1 without the second.

**Phase 3 — `POST /users`, create and invite — Done 2026-09-24.** 1252 unit +
579 integration tests (58 + 24 new), no migration, one new reason code. The
whole flow walked against the running API, invitation email included. Before
touching it:

- **`EmailAlreadyInUse` (Conflict, 409) is decided by `UQ_Users_Email` and
  nothing else.** No pre-check anywhere, deliberately: one able to see another
  tenant's row would need an unfiltered read, which §4.2 reserves for the two
  named authentication methods. So the refusal is race-free (§6 tier 1,
  uniqueness) and the code raising it **cannot** learn which tenant the
  collision is in — decision `0010` + AC-4, structurally rather than by
  discipline. `UserRepository.SaveChangesAsync` translates SQL 2601/2627 and
  checks the *index name*, since `Users` has two other unique indexes.
- **Save first, send second.** No invitation goes out for an account the
  database refused, and the account, its `Member` role and its activation token
  land in one save.
- **A created user gets `Member`.** This corrects the plan's premise that a
  roleless user "sees nothing": `AuthorizationPolicies.TenantMember` needs only
  the `orgId` claim, so roleless already means full member access. Nothing
  branches on `Role.Member`, so it grants nothing extra and makes the row honest
  — and matches what the seed does.
- **A send failure still returns 201** with the activation link and
  `invitationEmailSent: false` (plan §4.3). The provider's own detail never
  reaches the response — it can name hosts and accounts.
- **`Activation:ActivationUrl` is required with no default**, like
  `Cors:AllowedOrigins`. The API cannot guess its frontend's origin and a guess
  would email links that lead nowhere, so startup fails instead.
- **The create validator is narrower than login's**, by one rule: no whitespace
  in the email. FluentValidation's `.EmailAddress()` accepts
  `"ada lovelace@acme.test"` and MimeKit refuses it, so without this a paste-a-
  name typo produced a 201, a failed send and a colleague who never heard
  anything. Login is deliberately not tightened to match.
- **An admin still cannot see the colleague they just created.** `GET /users`
  remains the `0018` eligible-approver set, so a new Member is absent from it.
  Phase 4 is what closes that; until then the 201 body is the only record the
  admin gets.

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
