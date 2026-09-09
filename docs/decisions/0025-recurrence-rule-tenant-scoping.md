# 0025 — RecurrenceRules tenant scoping

## Status
Decided and implemented 2026-09-09, found while building WP-5 Phase 2's
whole-series cancel endpoint. Not an open question put to the owner — a bug
found during implementation, fixed the same way `0014` was, and recorded here
because CLAUDE.md §11 asks that a schema gap be said out loud rather than
routed around.

## Context
`RecurrenceRules` has existed since WP-1 with no `OrgId` column, no EF global
query filter, and no row-level-security predicate — the same gap `0014`
(`docs/decisions/0014-child-table-tenant-scoping.md`) closed for
`AvailabilityWindows` and `BlackoutPeriods` in WP-3, and for the identical
reason: a table reachable by resource is not automatically reachable *only*
through its resource once something reads or writes it by its own id.

The gap was latent rather than exploitable until now. WP-5 Phase 1's
`POST /recurrence-rules` only ever *creates* a rule, scoped implicitly by the
resource it is created for (`resource.OrgId`, itself already tenant-filtered);
nothing in Phase 1 loads an *existing* `RecurrenceRule` by a caller-supplied
id. Phase 2's `POST /recurrence-rules/{id}/cancel` is the first thing that
does — and it does so with decision `0002`'s reach, which for a `TenantAdmin`
means the owner filter is dropped entirely (`BookingOwnerFilter.AnyOwner`).
With no tenant restriction under that dropped filter, the query would have
been `WHERE Id = @id` and nothing else: a `TenantAdmin` in one tenant could
cancel another tenant's recurring series by knowing or guessing its GUID.

## Decision
Apply `0014`'s exact pattern to `RecurrenceRules`:

- **`OrgId`**, denormalized from the owning `Resource`, exactly as
  `BlackoutPeriod`'s already is (`0006`'s technique). `RecurrenceRule` is not
  part of the `Resource` aggregate — `Resource` has no navigation to it — so,
  like `BlackoutPeriod` and unlike `AvailabilityWindow`, the constructor stays
  public and takes `OrgId` from the resource its caller already loaded.
- **A composite same-org FK**, `FK_RecurrenceRules_Resources_SameOrg` against
  `Resources (OrgId, Id)`, so the denormalized value is physically unable to
  disagree with its resource's.
- **The EF global query filter**, `OrgId == _currentTenant.OrgId`, joining the
  five tables CLAUDE.md §4.2 already lists — this makes six.
- **The RLS filter predicate**, `Security.fn_TenantAccessPredicate(OrgId)` on
  `dbo.RecurrenceRules`, the same shared function every other tenant-owned
  table uses.

Migration `AddRecurrenceRuleTenantScoping`, hand-edited after scaffolding in
the same three ways `0014`'s migration was: `OrgId` added nullable, backfilled
from the owning `Resource`, then altered to `NOT NULL`; the backfill runs with
`Security.TenantAccessPolicy` switched off (a raw connection cannot bypass
its own already-initialized session context, so the policy itself has to
stand down for the statement); the filter predicate is added last, after the
column is finalized, because a security policy schema-binds the column it
filters and blocks `ALTER COLUMN` on it. Verified by a real revert and
re-apply against the dev database, per this project's convention for every
schema-carrying migration.

**The FK stays `NoAction`**, unlike `0014`'s `Cascade` for `AvailabilityWindow`
and `BlackoutPeriod` — a deliberate deviation, not an oversight. The
*original* single-column `FK_RecurrenceRules_Resources` was already `NoAction`
(CLAUDE.md §5's default), and widening the join to a composite key is not a
reason to also change its delete behavior; a `Resource` is never actually
deleted (§4.5), so the distinction is moot in practice, and the smaller
change is the safer one.

## Consequences
- `RecurrenceRule` implements `ITenantOwned`; its constructor takes `orgId` as
  its second parameter, matching `BlackoutPeriod`'s shape. Every call site —
  `CreateRecurrenceSeriesCommandRequestHandler`, `SeedData`, and every unit
  test constructing one directly — passes it.
- `IRecurrenceRuleRepository.FindForCancellationAsync` (WP-5 Phase 2) is the
  first read this fix protects: it reuses `BookingOwnerFilter` verbatim rather
  than a parallel type, since decision `0002`'s reach is identical regardless
  of which entity is being reached, and it relies entirely on the query filter
  above for the tenant half — no `OrgId` comparison is written in the handler,
  which is CLAUDE.md §4.2's point: isolation must not depend on a handler
  remembering one.
- `CancelRecurrenceSeriesEndpointTests.Cancel_RefusesAnAdminReachingIntoAnotherTenant`
  is the test that would have caught this gap, and does now — it asserts a
  `TenantAdmin`'s dropped owner filter still cannot reach across an org
  boundary, the same assertion `0014`'s and WP-4's own cross-tenant sweeps
  make for their tables.
- `SeedDataTests` and `TenantIsolationTests` needed the same `IgnoreQueryFilters()`
  treatment their `AvailabilityWindows`/`BlackoutPeriods` assertions already
  had — both had been reading `RecurrenceRules` with no filter to ignore
  because there was none yet, and both went from a real count to zero the
  moment the filter was added, which is exactly the fail-closed direction
  CLAUDE.md §4.2 asks for.
