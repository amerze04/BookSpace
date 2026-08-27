# 0013 — Structural tenant isolation: validation not assignment, and the RLS bypass signal

**Status:** Decided (2026-08-27)
**Requirements:** FR-1.1, FR-1.2
**Raised by:** WP-2 Phase 4.

## Decision

CLAUDE.md §4.2 prescribes three mechanisms. Two of them turned out to need a
more precise reading than their one-line description once built against this
codebase's actual domain shape:

**1. "OrgId set in SaveChangesAsync for added ITenantOwned entities" is a
validation guard, not an assignment.** `ITenantOwned.OrgId` is get-only, and
`User`/`Resource`/`Booking` all require `OrgId` at constructor time already —
there is no setter to reach, even reflectively across the assembly boundary,
without breaking the domain's own invariants (`Booking.OrgId` is even
DB-constrained to match its `Resource.OrgId` via
`FK_Bookings_Resources_SameOrg`). `BookSpaceDbContext.SaveChanges*` instead
walks `ChangeTracker.Entries()` for `Added`/`Modified` `ITenantOwned` entities
and throws `TenantIsolationViolationException` if the entity's `OrgId`
doesn't match `ICurrentTenant.OrgId` — catching an application-layer bug
before it reaches disk, not performing the assignment CLAUDE.md's wording
implies. Covers `Modified` as well as `Added`: no current domain method
reassigns `OrgId` after construction, but the check is the same equality
test either way, and it closes the door on a future one doing so by
accident — cheap insurance, not a literal reading of the CLAUDE.md text
("for added ITenantOwned entities"). When `ICurrentTenant.OrgId` is itself
null (SeedData, or a future SysAdmin-driven provisioning flow), validation
is skipped entirely — there's nothing to compare against, and refusing would
break seeding.

**2. EF's `IgnoreQueryFilters()` and SQL Server RLS are independent layers,
and RLS needs its own explicit bypass signal.** RLS is enforced by the
engine itself via `SESSION_CONTEXT`, completely independent of what LINQ
generates. `AuthenticationUserRepository` (the one existing sanctioned
`IgnoreQueryFilters()` caller — login runs before any tenant context exists)
would go from "unfiltered at the ORM layer" to "silently zero rows at the DB
layer" the moment RLS landed, unless RLS itself was told this specific query
is deliberately scopeless. The naive fix — treat `SESSION_CONTEXT('OrgId')
IS NULL` as "allow all" — was rejected: it inverts the fail-closed default
for every uninitialized connection (a raw ad hoc query, a bug), not just the
one sanctioned bypass path. Instead:
- `TenantBypassScope` (`BookSpace.Infrastructure.Persistence`), an
  `AsyncLocal<bool>`-backed ambient scope, entered only by
  `AuthenticationUserRepository.QueryUnfiltered`.
- `TenantSessionContextInterceptor` reads it on every `ConnectionOpened` and
  sets a `TenantBypass` session-context key alongside `OrgId`.
- The RLS predicate function only allows an `OrgId` mismatch through when
  `TenantBypass = 1` — never merely because `OrgId` is unset.

A third key, `TenantInit`, is set unconditionally to `1` on every connection
open and required by the predicate before anything else is evaluated. This
closes a subtler gap than "NULL means allow": without it, a null-`OrgId`
SysAdmin `Users` row and a connection nobody ever called
`sp_set_session_context` on are indistinguishable to a naive null-safe
predicate, which would leak SysAdmin rows to any untouched connection.
Requiring `TenantInit = 1` makes "never touched by the interceptor" and
"legitimately scoped to a SysAdmin" resolve differently.

## Why

**Validation over assignment**, beyond the pure impossibility argument
above: the entities already get `OrgId` right at construction because the
Application layer that builds them necessarily knows the tenant (it has to,
to satisfy `FK_Bookings_Resources_SameOrg` and similar invariants) — a
`SaveChanges*`-time stamp would be redundant with correct code and useless
against incorrect code (nothing to overwrite a wrong value *to*, since the
constructor already ran). A validation guard is the version of "enforce this
structurally" that's actually implementable given the domain already
requires `OrgId` up front.

**A third session-context key instead of reusing NULL.** Two independent
things can make `SESSION_CONTEXT('OrgId')` read as NULL: a SysAdmin's own
session (legitimately no tenant), and a connection the interceptor never
touched (a bug, or a raw ad hoc query — exactly what RLS exists to catch).
Collapsing those into one signal would have quietly reopened the
uninitialized-connection hole this whole mechanism exists to close.
`TenantInit` disambiguates them for the cost of one extra session-context
call.

**Filter predicate only, no block predicate.** A block predicate would need
to special-case `SeedData`'s inserts (two tenants' users plus a null-OrgId
SysAdmin, no per-tenant session context active) to avoid breaking seeding
and every seed-dependent test fixture. That protection is already covered on
the write side by the stored-procedure gate (§4.1, for `Bookings`) and the
`SaveChanges*` validation above (for `Users`/`Resources`). Revisit once a
real SysAdmin/TenantAdmin provisioning write path exists, so the block
predicate's bypass condition can be designed against a concrete flow instead
of guessed in the abstract.

## Consequences

- `ICurrentTenant` (`BookSpace.Application.Abstractions`) is the single read
  side both mechanisms key off: `Guid? OrgId`, null meaning SysAdmin or "no
  request in flight." Implemented in `BookSpace.Api`
  (`HttpContextCurrentTenant`, reading the `orgId` claim from `0009`) rather
  than in `BookSpace.Infrastructure`, which has no ASP.NET Core dependency
  and shouldn't gain one for this one class.
- `TenantBypassScope.Enter()` may only be called from
  `AuthenticationUserRepository`, for the same reason CLAUDE.md §4.2
  restricts `IgnoreQueryFilters()` to explicitly named methods — the
  restriction now covers both layers uniformly. `SeedData` does not need it:
  its idempotency check queries `Organizations` (never tenant-owned or
  RLS-protected), and it only ever inserts, never re-reads what it just
  wrote, so the filter-predicate-only policy never blocks it.
- `TenantIsolationViolationException` maps to a generic 500 in
  `GlobalExceptionHandler`, deliberately opaque to the client — it signals an
  application bug, not something a caller should ever learn tenant-boundary
  information from. The `reasonCode` exists to be greppable in logs.
- Proved at two independent levels: `TenantOwnershipValidationTests` /
  `GlobalQueryFilterTests` (EF InMemory — `ChangeTracker`/LINQ logic only, not
  constraints or RLS, so CLAUDE.md §8's real-SQL-Server rule doesn't apply to
  them) and `TenantIsolationTests` (real SQL Server, through the real HTTP
  pipeline and via a raw `SqlConnection` bypassing EF entirely — the latter
  is what actually proves RLS, independent of the application behaving
  correctly).

## Notes

No `Bookings` rows are seeded yet (`CLAUDE.md` §4.1 — `dbo.CreateBooking`
doesn't exist), so the `Bookings` cross-tenant leak test is currently a smoke
check only (filter clause builds, returns empty, doesn't throw). Add the real
leak test once a Bookings write/read path exists.
