# 0014 — AvailabilityWindows and BlackoutPeriods get their own OrgId

**Status:** Decided (2026-08-28), implemented (2026-08-31)
**Requirements:** FR-1.2, FR-3.2, FR-3.4
**Raised by:** WP-3 planning (`docs/wp3-plan.md`, decision D1), before any WP-3
code was written.

## Context

`AvailabilityWindows` and `BlackoutPeriods` sat **outside all three** of
CLAUDE.md §4.2's isolation mechanisms. Neither implemented `ITenantOwned`,
neither had an `OrgId` column, neither appeared in `BookSpaceDbContext`'s global
query filters, and `Security.TenantAccessPolicy` (the `AddTenantIsolationRls`
migration) covered only `dbo.Users`, `dbo.Resources` and `dbo.Bookings`. Both
tables were reached purely by `ResourceId`.

That was harmless through WP-2 only because nothing queried them. WP-3 is the
first work package whose endpoints operate mostly *on* them, and written the
obvious way a handler would be

```csharp
await _context.BlackoutPeriods.FirstOrDefaultAsync(b => b.Id == id, ct);
```

— which returns another tenant's blackout, with no filter and no RLS to stop
it. FR-1.2 requires isolation that cannot be bypassed by forgetting a filter,
so this had to close before any WP-3 endpoint existed.

## Decision

**Denormalize.** Both tables get their own `OrgId`, both entities implement
`ITenantOwned`, and both are added to the global query filters and to the RLS
security policy. The two values are then made physically unable to disagree by
a composite foreign key against `Resources`' `(OrgId, Id)` alternate key
(`UQ_Resources_Org_Id`) — `FK_AvailabilityWindows_Resources_SameOrg` and
`FK_BlackoutPeriods_Resources_SameOrg`, replacing the previous single-column
FKs.

This is decision [`0006`](0006-orgid-denormalization.md)'s house precedent
applied unchanged: duplicate the column when it buys enforceable isolation, and
back it with a composite FK so the duplicate cannot drift.

**Rejected alternative:** an architectural rule that the two tables are only
ever reached through the (already filtered) `Resource` aggregate. That works,
costs no migration, and is the smaller change — but it is a *convention*, and
§4.2's entire premise is that isolation must not depend on remembering one.
The owner's reasoning at approval time: a migration is still cheap at this
stage, and this is a core requirement rather than a nicety.

### How the `OrgId` gets set

The two tables differ, and the difference is deliberate:

- **`AvailabilityWindow` is part of the `Resource` aggregate.** Its constructor
  is `internal` to `BookSpace.Domain`, and `Resource.AddAvailabilityWindow`
  (which takes primitives and stamps its own `OrgId` and `Id`) is the only
  caller. A window whose `OrgId` disagrees with its resource's is therefore
  *unconstructible*, not merely rejected later.
- **`BlackoutPeriod` is not.** `Resource` has no navigation to it, so there is
  no aggregate root to stamp from; its constructor stays public and takes
  `orgId` from the resource the caller loaded. Here the composite FK is the
  only thing standing between a caller and a mismatch — which is exactly what
  it is for.

## Consequences

- Migration `20260831085008_AddChildTableTenantScoping`. Three details in it
  are hand-written rather than scaffolded, each documented in the file:
  1. `OrgId` is added **nullable**, backfilled from the owning resource, then
     altered to `NOT NULL`. EF's scaffold wanted `NOT NULL DEFAULT
     '00000000-0000-…'`, which would stamp every existing row with an `OrgId`
     no `Organization` owns and then fail the new composite FK.
  2. `Security.TenantAccessPolicy` is switched `OFF` around the backfill. The
     backfill reads `dbo.Resources`, which the policy already filters, and the
     migration's own connection cannot grant itself a bypass —
     `TenantSessionContextInterceptor` sets the session context with
     `@read_only = 1`, so `TenantBypass` cannot be re-set for a session it has
     already initialized. Without the toggle the join matches zero rows, the
     backfill is a **silent no-op**, and the following `ALTER COLUMN` fails on
     the NULLs it left behind. The policy is therefore briefly off for every
     session; acceptable for a schema migration, and the reason this is the
     only place that does it.
  3. The two filter predicates are added **last**. A filter predicate
     schema-binds the policy to the column, and SQL Server will not let
     `ALTER COLUMN` touch a column a security policy references.
- **CLAUDE.md §4.2's mechanism list changes meaning**: the global query filters
  and the RLS policy now name five tables, not three. `ITenantOwned` is no
  longer a marker carried only by aggregate roots.
- `IX_AvailabilityWindows_ResourceId` (EF's auto FK index) is superseded by
  `IX_AvailabilityWindows_OrgId_ResourceId`. `BlackoutPeriods` gains the
  equivalent composite index; its hand-named
  `IX_BlackoutPeriods_Resource_Start` is unchanged.
- Any test or fixture reading these tables **without** a current tenant now
  needs `IgnoreQueryFilters()` — `TenantBypassScope` covers RLS only, never the
  EF filter. `SeedDataTests`' row-count assertions needed exactly this.
- Proved at three levels: EF InMemory unit tests over the filter expressions
  and the `SaveChanges*` guard; integration tests through the real HTTP
  pipeline with a real authenticated principal; and a raw `SqlConnection` with
  no EF involved, which is what actually proves RLS. The composite FK's
  rejection of a cross-tenant insert was additionally confirmed by hand
  against the dev database.

## Notes

The obvious-but-wrong handler shape from the Context section above now lives in
`TenantIsolationProbeController.BlackoutById` as a test probe — filtered by
`Id` alone, on purpose — so the integration suite keeps asserting that the two
mechanisms behind it are what make it safe.
