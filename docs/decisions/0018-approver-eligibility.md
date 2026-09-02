# 0018 — Who may be assigned as a resource approver

**Status:** Decided 2026-09-01 by the repo owner, implemented 2026-09-02 in
WP-3 Phase 3 step 2.
**Requirement:** FR-3.3 — "a resource can be marked `RequiresApproval`, with one
or more assigned approvers".
**Reason code:** `ApproverNotEligible` (`ErrorKind.RuleViolation` → 422).

---

## The question

`PUT /resources/{id}/approvers` takes a list of user ids. Something has to decide
which users a TenantAdmin may put in that list. `Resource.AddApprover` takes a
bare `Guid` and checks only for duplicates, so without a rule an admin could
assign anyone — including a user in another tenant, which is a cross-tenant write
dressed up as an ordinary assignment.

Three readings were on the table:

1. Only users holding `Role.Approver`.
2. Users holding `Role.Approver` **or** `Role.TenantAdmin`.
3. Any active user in the tenant; the assignment itself is what makes someone an
   approver of that resource.

---

## Decision

**Eligible = in the caller's own tenant, `IsActive`, and holding `Approver` or
`TenantAdmin`.** Reading 2, plus the active check.

### Why not reading 1 (Approver role only)

`AuthorizationPolicies.Approver` (decision `0012`) already admits
`Approver`, `TenantAdmin` **and** `SysAdmin`. Under reading 1 a TenantAdmin could
not be *assigned* to a resource they are nonetheless entitled to *approve* — the
set that may hold the job and the set that may do it would disagree, which is the
kind of mismatch nobody notices until an approval silently cannot be actioned.

It is also unworkable today for a practical reason: there are no
user-management endpoints, so nothing in the API can grant `Role.Approver`. Each
tenant seeds exactly one such account (`approver@acme.test`), which under reading
1 would be the only assignable person in the entire tenant.

`SysAdmin` is excluded automatically rather than by a special case: a SysAdmin has
no `orgId` claim (decision `0009`), so they are in no tenant, and the
tenant-filtered user query never returns them. PRD §2 wants exactly that — the
Platform Operator is not part of a tenant's approval chain.

### Why not reading 3 (any active tenant user)

It would make the role decorative here while leaving it load-bearing in WP-4: the
approval endpoint will sit behind `AuthorizationPolicies.Approver`, so an assigned
Member would be refused at the moment they tried to approve. Reading 2 keeps
"may be assigned" and "may approve" as one set, so that failure cannot arise.

### Why `IsActive` is part of eligibility

A deactivated approver does not fail loudly — approvals routed to them simply
never get actioned, and the stale-approval expiry job (§7) eventually kills the
request. Refusing the assignment turns a silent stall into an immediate 422.


**Note on eligibility timing (settled 2026-09-02).** Eligibility is checked **at
assignment time only**, and the repo owner confirmed that is the intended
behaviour rather than a gap. Nothing re-checks an existing assignment when a user
is later deactivated or loses the role, so a resource can hold an approver who no
longer qualifies. What should happen then — drop the assignment, refuse the
booking, or escalate to the TenantAdmin — is a question for **WP-4**, which owns
approval routing and is where the consequence lands: a request routed to an
inactive approver stalls until the stale-approval expiry job (§7) expires it.

---

## One code for three causes

All three failures — wrong tenant, wrong roles, deactivated — return the same
`ApproverNotEligible`, and the response says nothing about which applied.

That is the same reasoning that renamed the code from `ApproverNotInTenant` in
decision `0016`: a code meaning "not in *your* tenant" confirms the id exists
somewhere, which is the cross-tenant disclosure AC-4 forbids. Splitting the code
three ways would re-open the hole the rename closed.

The mechanism backs the promise up rather than relying on the handler to be
careful. `IUserRepository` runs every query through the tenant-filtered `DbSet`
with no `IgnoreQueryFilters` and no `TenantBypassScope`, so a cross-tenant id
never comes back at all — the handler *cannot* report it as anything more
specific, because it does not know. `IntegrationTests` asserts the stronger
property directly: a Globex approver's real id and a `Guid.NewGuid()` produce
byte-identical problem bodies once the correlation id is stripped.

`RuleViolation`, not `NotFound`: within the caller's tenant the user genuinely
does not exist, but the request is a well-formed assignment refused by a rule, and
a 404 on a `PUT` whose *own* path resource exists reads as "the room is missing".

---

## Consequences

- **The Phase 2 gap closes.** `ResourceWriteRules.EnsureApproversWhenRequired`
  refused `RequiresApproval = true` on create and edit, which meant that until
  this endpoint existed only a resource that somehow already had an approver could
  carry the flag. An admin can now publish an approval-gated resource in two
  calls: assign approvers, then set the flag.
- **The invariant is now enforced from both sides.** Emptying the approver list on
  a resource that requires approval throws `ApproversRequired` (422). Without
  that, an admin could set the flag with an approver assigned and then clear the
  list, landing in exactly the state FR-3.3 rules out — through the side door.
- **Approvers are managed by replace-the-set (`PUT`), not per-row POST/DELETE.**
  This follows from the invariant rather than from symmetry with the availability
  windows: swapping one approver for another with per-row endpoints has to pass
  through the empty list, which the rule above refuses. A whole new set in one
  request has no invalid intermediate state.
- **The read detail shows approver names to any `TenantMember`**, not only to
  admins — a member deciding whether to book an approval-gated room should be able
  to see who will be deciding. Names only, never email addresses: a bare `Guid`
  tells a member nothing, and an address tells them more than they asked for.
- **The role test runs in memory, not in SQL.** `User.Roles` is a computed
  property over the owned `_roleAssignments` collection and has no translation;
  pushing it into the query would mean an `EF.Property` expression over a private
  backing field. The candidate set is one approver list — a handful of rows — so
  `UserRepository` loads them and applies the rule in the domain's own vocabulary.
  `IsActive` stays in the SQL, because it is a plain column and filtering there
  keeps a deactivated user out of memory rather than merely out of the answer.
  `AuthenticationUserRepository` makes the same trade for the same reason.
- **Both lists are capped** — 100 availability windows, 50 approvers per request,
  rejected rather than truncated. Not a guess at a real limit: a ceiling on an
  otherwise unbounded write, since nothing else stopped one authenticated admin
  sending ten thousand rows in a single INSERT. Deliberately generous, so that a
  request meeting the cap is a signal worth reading rather than a limit worth
  raising. Delegated to the assistant's judgment by the owner on 2026-09-02.

---

## Notes

`IUserRepository` is deliberately the mirror image of
`IAuthenticationUserRepository`. The latter is the one sanctioned
`IgnoreQueryFilters()` path in the codebase, because login runs before a tenant
context exists (CLAUDE.md §4.2); the former bypasses nothing, and the contrast is
what makes the isolation argument above hold without a single `OrgId` comparison
written in application code.
