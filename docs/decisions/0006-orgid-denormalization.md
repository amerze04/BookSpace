# 0006 — OrgId denormalization on Bookings and Resources

## Status
Decided — formalizes an already-enforced rule, prompted by mentor ERD feedback (`docs/Amer-ERD-Feedback.docx`)

## Context
`Bookings.OrgId` and `Resources.OrgId` both exist directly on their tables,
even though `Bookings.OrgId` is also reachable indirectly via
`Bookings.ResourceId → Resources.OrgId`. The mentor's feedback flagged this
as a potential redundancy: "Two paths to the same tenant can disagree... if
the denormalization is deliberate, write down the rule... and how you
enforce it."

## Decision
The denormalization is deliberate, for two reasons:
1. **Faster tenant-scoped queries.** Every tenant-scoped `Bookings` query
   (which is most of them — `IX_Bookings_User`, RLS, the global query
   filter) filters directly on `Bookings.OrgId` without joining through
   `Resources`.
2. **More enforceable tenant isolation.** `Bookings.OrgId` feeds directly
   into the SQL Server row-level-security session context
   (`sp_set_session_context`, CLAUDE.md §4.2) and the EF global query filter
   without a join. FR-1.2's isolation requirement has to be structural, not
   a query author remembering to join through `Resources` correctly every
   time.

Critically, the two values are **not just conventionally kept in sync** —
they cannot disagree. The composite foreign key
`FK_Bookings_Resources_SameOrg FOREIGN KEY (OrgId, ResourceId) REFERENCES
Resources (OrgId, Id)` (against the alternate key `UQ_Resources_Org_Id`)
means SQL Server physically rejects any `Bookings` insert or update where
`OrgId` doesn't match the referenced `Resources.OrgId`. This is a real
database constraint, not a documented convention that could silently drift.

## Consequences
- No schema change — this decision documents what
  `FK_Bookings_Resources_SameOrg` already enforces.
- `Resources.OrgId` itself is not redundant with anything; it's the
  resource's own direct tenant ownership, not derived from another table.
