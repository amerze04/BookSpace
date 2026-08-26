# 0012 — RBAC is enforced with named policies and a deny-by-default fallback

**Status:** Decided (2026-08-26)
**Requirements:** FR-1.4, FR-1.5, PRD §2
**Raised by:** WP-2 Phase 3.

## Decision

Four named authorization policies, defined once in
`BookSpace.Api/Authorization/AuthorizationPolicies.cs`:

| Policy | Satisfied by |
|---|---|
| `SysAdminOnly` | `SysAdmin` |
| `TenantAdmin` | `TenantAdmin`, `SysAdmin` |
| `Approver` | `Approver`, `TenantAdmin`, `SysAdmin` |
| `TenantMember` | any authenticated principal **with an `orgId` claim** |

Plus `FallbackPolicy = RequireAuthenticatedUser()`, so every endpoint is
protected unless it opts out with `[AllowAnonymous]`.

## Why

**Policies, not `[Authorize(Roles = "...")]` per endpoint.** Most admin-level
endpoints should accept both `TenantAdmin` and `SysAdmin`. Expressed as role
attributes that becomes `Roles = "TenantAdmin,SysAdmin"` repeated at every call
site — and the work package is explicit that a rule copy-pasted across forty
methods is the wrong answer, for tenant filtering and for the same reason here:
the forty-first is the one that gets it wrong. One named policy is one place to
be correct, and one place for Phase 4 to add the tenant-scoping requirement.

**Deny by default.** The WP-2 criterion is that *every* endpoint enforces
authorization server-side. Opt-in protection cannot deliver that — it is always
one forgotten attribute away from a public endpoint, and nothing fails when
someone forgets. With a fallback policy the failure mode inverts: forget the
attribute and the endpoint is closed, which is noticed immediately. The cost is
that genuinely public endpoints must be marked; `HealthController` and
`AuthController` are, and `PolicyProbeController` in the integration tests
asserts that an unannotated endpoint really is closed.

**`TenantMember` deliberately excludes SysAdmin.** This is the one place the
hierarchy is broken on purpose. PRD §2: the Platform Operator "must never see
tenant booking content in the course of routine operation." So SysAdmin is a
separate axis, not the top of a ladder — it can administer tenants without being
able to read inside one. Requiring the `orgId` claim expresses this directly,
because a SysAdmin has no such claim (`0009`). A SysAdmin who genuinely needs to
read tenant data does it through an explicitly named SysAdmin path, matching the
`IgnoreQueryFilters()` rule in `CLAUDE.md` §4.2.

**Roles come from the token, not the database, per request.** The role claims are
minted at login and last as long as the access token — at most 15 minutes. A
role change therefore takes effect on the next refresh, the same boundary FR-2.4
uses for deactivation (`0011`). Checking roles against the database on every
request would make them instant at the cost of a query per request, which is the
cost stateless tokens exist to avoid.

## Consequences

- Adding a controller requires no thought about authorization to be *safe* — the
  fallback covers it. It requires thought to be *correct*, which is the right
  way round.
- Phase 4 adds tenant scoping inside these policies (or as a requirement
  alongside them), so no endpoint annotation has to change when it lands.
- `AuthorizationPolicyTests` covers each policy from each seeded role, including
  the two asymmetries worth remembering: `TenantAdmin` satisfies `Approver`
  without holding the Approver role, and `SysAdmin` is **forbidden** from
  `TenantMember`.

## Notes

Authorization currently distinguishes 401 (no or invalid token) from 403
(authenticated, not permitted), which is standard and is asserted in the tests.
Tenant isolation — a valid token from tenant A being unable to reach tenant B's
rows even with a forged identifier — is **not** covered by these policies and
remains Phase 4's job.
