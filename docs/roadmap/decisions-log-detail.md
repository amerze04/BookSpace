_Extracted from CLAUDE.md §9 during the 2026-09-16 file-size reduction pass._

_CLAUDE.md §9 keeps the numbered index (one line per decision, linking to its
`docs/decisions/000N-*.md` file). This file preserves the fuller per-decision
summaries — including amendment reasoning — that used to sit inline in
CLAUDE.md. The authoritative reasoning for any one decision is always its own
file under `docs/decisions/`; this file and CLAUDE.md's index are both just
navigation aids on top of that._

---

PRD §13 left several decisions to the team; schema/ERD review surfaced a few
more. **All are resolved as of 2026-08-26** (0001–0008 on 2026-08-19;
0009–0012 with WP-2 Phase 3) and written up individually in
`docs/decisions/` — read the linked doc before touching the affected
feature, the reasoning matters as much as the answer.

1. [`0001`](../decisions/0001-blackout-vs-recurring-series.md) — A blackout has
   absolute priority over a recurring series: it cancels every occurrence it
   overlaps (any status), and the booking's owner is notified.
2. [`0002`](../decisions/0002-tenant-admin-cancellation.md) — A TenantAdmin can
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
3. [`0003`](../decisions/0003-availability-timezone.md) — Availability is
   expressed in the resource's timezone, not the booker's.
4. [`0004`](../decisions/0004-no-show-definition.md) — No-show = `Confirmed`,
   never checked in, past `StartsAtUtc + Organizations.NoShowGraceMinutes`.
   Determined by the no-show release job (system-initiated, not a person —
   `Bookings.UpdatedByUserId` stays null on this transition).
5. [`0005`](../decisions/0005-capacity-semantics.md) — `Capacity` means
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
6. [`0006`](../decisions/0006-orgid-denormalization.md) — `Bookings.OrgId`
   duplicating `Resources.OrgId` is deliberate (query performance +
   enforceable isolation), not accidental drift — and it's not just
   convention: `FK_Bookings_Resources_SameOrg` makes the two values
   physically unable to disagree.
7. [`0007`](../decisions/0007-recurrence-materialization-horizon.md) — A
   recurring series is fully materialized at creation (best-effort per
   occurrence, not atomic), capped at two calendar years past its own
   `StartDate`. No background top-up job.
8. [`0008`](../decisions/0008-dst-spring-forward-policy.md) — An occurrence
   whose local time falls in a DST spring-forward gap is skipped, not
   shifted. The user is told immediately (in the series-creation response)
   and again by email 14 days before the date, via the existing Reminder
   dispatch job — no new job. `Notifications` gained a second anchor
   (`RecurrenceRuleId` + `OccurrenceDate`) for this one case, since there's
   no `Booking` to reference.
9. [`0009`](../decisions/0009-jwt-claims-and-token-lifetimes.md) — Access
   tokens are HMAC-SHA256 JWTs carrying `sub`, `email`, `orgId`, and one `role`
   claim per role. `orgId` is **omitted entirely** for a SysAdmin rather than
   emitted empty, so no code can mistake it for a real tenant. 15-minute access
   token, 14-day **absolute** refresh window. Signing key from configuration
   only, validated at startup.
10. [`0010`](../decisions/0010-global-email-uniqueness.md) — An email
    identifies exactly one user platform-wide (`UQ_Users_Email`, unfiltered),
    so login takes email + password with no tenant discriminator. Replaced the
    WP-1 `(OrgId, Email)` filtered index, which allowed the same email in two
    tenants and left SysAdmin rows with no uniqueness at all. **Decided by the
    repo owner.**
11. [`0011`](../decisions/0011-refresh-token-hashing-and-rotation.md) —
    Refresh tokens are 256-bit CSPRNG values stored as SHA-256 (deterministic,
    because lookup is *by* hash; a salted KDF would break the index and protect
    nothing at that entropy). Rotation keeps the `FamilyId` and inherits the
    original expiry. **Reuse of a revoked token kills the whole family**;
    expiry kills only that token.
12. [`0012`](../decisions/0012-rbac-enforcement-model.md) — RBAC via four
    named policies plus a deny-by-default `FallbackPolicy`, so a new endpoint is
    protected unless it opts out. `TenantMember` deliberately **excludes**
    SysAdmin (PRD §2: the Platform Operator must never see tenant booking
    content in routine operation).
13. [`0013`](../decisions/0013-tenant-isolation-mechanism.md) — Structural
    tenant isolation is validation, not assignment: `SaveChanges*` throws if an
    `ITenantOwned` entity's `OrgId` doesn't match the current tenant, since the
    property has no setter to "stamp." RLS gets its own explicit bypass signal
    (`TenantBypassScope` + a `TenantInit`/`TenantBypass` session-context pair)
    rather than treating an unset session as "allow all."
14. [`0014`](../decisions/0014-child-table-tenant-scoping.md) — `AvailabilityWindows`
    and `BlackoutPeriods` carry their own `OrgId` and fall inside all three
    §4.2 mechanisms, instead of being reached by `ResourceId` alone. Composite
    FKs against `UQ_Resources_Org_Id` make the denormalized value unable to
    disagree with its resource's — decision `0006`'s technique, reapplied.
    `AvailabilityWindow`'s constructor is `internal`, so `Resource` is its only
    creator; `BlackoutPeriod`'s stays public because it sits outside that
    aggregate. **Promoted from WP-3's D1** when Phase 1 landed.
15. [`0015`](../decisions/0015-api-contract-and-pagination.md) — API contract
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
    **Amended 2026-09-01 on the mentor's advice**: every `IRequest<T>`
    implementation is named `…CommandRequest` / `…QueryRequest`, with its
    handler and validator following suit; and **response DTOs are
    per-endpoint, in their own files, never shared** — even when the fields
    are currently identical, because Phase 3's availability windows belong on
    the read detail and would otherwise appear in the create and archive
    responses too. `PagedResult<T>` stays shared (it is the envelope, not a
    response), and `IssuedTokens` — formerly `AuthenticationResult` — is
    `TokenIssuer`'s output rather than any endpoint's contract. One wire
    change: `PUT /resources/{id}` is now flat instead of wrapping the resource
    in a `resource` property.
16. [`0016`](../decisions/0016-error-contract-and-reason-codes.md) — one error
    contract: a handler rejects a request by throwing an `AppException`, and
    `GlobalExceptionHandler` maps `ErrorKind` —
    Validation/Unauthorized/NotFound/Conflict/RuleViolation — onto
    400/401/404/409/**422** once. A new failure needs a code and a kind, not a
    new switch case. The exception **message never reaches the response** (log
    only); `Title` is generic per kind and the reason code carries the meaning.
    Also holds the reason-code catalogue (`ReasonCodes`), §6's list in code,
    with each code's kind beside it; authentication's five stay in
    `AuthenticationFailureReason`, and a test proves every code is unique
    across both files and matches its own member name.
    **Amended 2026-09-01 on the mentor's advice**: `AppException` is now
    **abstract with a protected constructor**, and each failure is a named
    `sealed` subclass fixing its own kind and code, so the two can no longer
    be paired wrongly.
17. [`0017`](../decisions/0017-test-fixture-booking-inserts.md) — integration
    **test fixtures** may insert `Bookings` rows with **raw SQL** — never LINQ,
    never `SaveChanges`, and only in fixtures. A narrow, documented carve-out
    from §4.1, because `CapacityBelowExistingBookings` (WP-3 Phase 2) and the
    availability query's "excludes existing bookings" AC (Phase 5) cannot be
    tested before `dbo.CreateBooking` exists in WP-4.
    **Gotcha recorded there**: the fixture connection needs an explicit RLS
    bypass, or the `INSERT`'s own `SELECT` (and the cleanup `DELETE`) silently
    affects zero rows. **Promoted from WP-3's D4** when Phase 2 step 3 landed.
    **Amended 2026-09-08** (WP-4): now that the procedure exists the carve-out
    **narrows rather than expires** — new tests and `SeedData` use the real
    path, and raw SQL in a fixture is for a state the API *cannot* reach (a
    wholly-past or `NoShow` booking) or for bulk (260 rows where 260 HTTP
    calls would dominate the measurement). Second gotcha: a fixture's instants
    must be truncated to whole seconds, or `datetime2(0)`'s rounding moves a
    stored boundary and turns an adjacency test intermittent.
18. [`0018`](../decisions/0018-approver-eligibility.md) — a resource approver
    must be **in the caller's own tenant, active, and hold `Approver` or
    `TenantAdmin`** — the same set `AuthorizationPolicies.Approver` admits, so
    "may be assigned" and "may approve" cannot disagree. All three failures
    return one `ApproverNotEligible`, saying nothing about which applied,
    because naming the tenant case would confirm a cross-tenant id exists
    (AC-4). Approvers are managed by replace-the-set `PUT`, which follows from
    the invariant rather than from symmetry: per-row endpoints would have to
    pass through the empty list, and an empty list on a resource requiring
    approval is refused.
19. [`0019`](../decisions/0019-blackout-period-lifecycle.md) — blackout periods
    get **full CRUD** (FR-3.4 only asked for "define"); **overlapping
    blackouts are allowed**, deliberately unlike availability windows, because
    the union of two blackouts is still blacked out and nothing contradicts;
    `DELETE` is a **real hard delete**, the first in this system, it
    **un-cancels nothing**, and it is deliberately **not idempotent** (a
    second `DELETE` is 404); the edit's cascade runs **forwards only**. A
    blackout **entirely** in the past is refused (`BlackoutPeriodElapsed`,
    422); one that merely *starts* in the past is not. The cascade cancels
    `Pending`/`Confirmed` **and only bookings that have not yet ended**,
    which is necessary rather than tidy: **nothing in this system writes
    `BookingStatus.Completed`**, so status alone would let a blackout rewrite
    history.
20. [`0020`](../decisions/0020-bookable-interval-semantics.md) — a bookable
    slot is a **free interval carrying `remainingCapacity`**, not a boolean
    free/busy timeline and not a fixed grid — forced by `0005`'s
    concurrent-units model. `BookableInterval` refuses a remaining capacity of
    zero so its name cannot be false. **WP-3's D2.**
    **Amended 2026-09-04**: the answer is now **per quantity**. The endpoint
    takes an optional `quantity` (default 1), a segment that cannot hold that
    many units is a **wall**, and everything between two walls is one interval
    carrying the **floor** across it. That replaced cutting at every capacity
    change, which let one 1-unit booking mid-day delete the bookable spans
    either side of it from the answer. Nothing changes for an exclusive
    resource, where the parameter can only be 1.
21. [`0021`](../decisions/0021-daylight-saving-for-availability-ranges.md) — a
    DST gap or doubling inside an availability *window* is **absorbed by
    expanding to the UTC interval that actually elapsed**: the local day is
    simply 23 or 25 hours long. A **missing** local time resolves to the
    **transition instant**, and an **ambiguous** one resolves to the
    **earlier** instant for a window's start and the **later** for its end
    (`TimeZoneInfo`'s own defaults get both wrong). **WP-3's D3.**
22. [`0022`](../decisions/0022-availability-window-midnight-convention.md) — a
    `ClosesAt` of exactly **`23:59:59` means the following midnight**,
    unconditionally. `CK_AvailabilityWindows_Window` forbids a window crossing
    midnight and `time(0)` cannot express `24:00:00`, so `23:59:59` is the
    only way an admin can say "until midnight."
23. [`0023`](../decisions/0023-booking-concurrency-strategy.md) — the booking
    concurrency strategy (WP-4's hard problem, FR-4.2, AC-1):
    `dbo.CreateBooking` takes **`UPDLOCK, HOLDLOCK` key-range locks** on the
    overlapping rows and compares the **peak concurrent** quantity — not the
    sum — against capacity. `HOLDLOCK` locks the gaps; `UPDLOCK` makes
    contenders *block* rather than deadlock on their inserts;
    `IX_Bookings_Resource_Start` keeps the range to one resource; 1205 retry
    is part of the design. `sp_getapplock` was the main rejected alternative.
    Measured: without the hints, 20 simultaneous requests for one slot
    produced **3** bookings and a pool of 4 was filled **10** times.
    **Evidence extended 2026-09-08** with HTTP-level figures (ten confirmed
    bookings on a capacity-1 room with the hints removed) and measured
    deadlock rates (1205 fires routinely at ten-way contention, every one
    absorbed by the retry). **Evidence extended again 2026-09-09** for
    `dbo.ApproveBooking`'s own distinct race (two decisions on one existing
    row): without the lock, seven of ten simultaneous approval pairs both
    reported Approved; every run of the correct procedure deadlocked at least
    once, always absorbed by the retry.
24. [`0024`](../decisions/0024-dst-fallback-recurrence-policy.md) — the DST
    **fall-back** case for a recurring occurrence resolves to the **earlier**
    of its two candidate UTC instants, for **both** the occurrence's start and
    its end — not `0021`'s start-earlier/end-later split, which is a property
    of a *range* allowed to stretch to 25 hours. No occurrence is skipped
    (unlike spring-forward, `0008`): both instants are real. This was §9's
    last open item; it is now closed.
25. [`0025`](../decisions/0025-recurrence-rule-tenant-scoping.md) —
    `RecurrenceRules` gets its own `OrgId`, joining the global query filters,
    RLS predicate and a composite same-org FK against `Resources` — the same
    fix `0014` gave `AvailabilityWindows`/`BlackoutPeriods` in WP-3, applied
    here because it was missing entirely. Found while building WP-5 Phase 2's
    cancel endpoint, the first thing that ever loaded a `RecurrenceRule` by a
    caller-supplied id rather than only creating one scoped by its resource.
26. [`0026`](../decisions/0026-notifications-series-anchor.md) — widens
    `CK_Notifications_HasContext` so `RecurrenceRuleId` alone (no
    `OccurrenceDate`) is a valid anchor, for WP-5 Phase 2's whole-series-cancel
    notification (`NotificationKind.SeriesCancelled`) — one summary row for
    the series, not tied to any single occurrence's date. A strict widening of
    decision `0008`'s original constraint.

If a task needs a decision that isn't listed above and isn't in this log,
**stop and ask** rather than picking silently.
