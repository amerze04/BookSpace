# 0031 — The last-TenantAdmin guard

## Status
Decided and implemented 2026-09-24, user management phase 5.

## Context

Phase 5 adds the three writes an administrator needs over their colleagues:
deactivate, reactivate, and replace a role set. Two of them can take the last
active `TenantAdmin` out of a tenant — by deactivating that account, or by
removing the role from it.

Nothing else in the system would notice. `Users.IsActive` and `UserRoles` carry
no constraint that could express "this tenant still has an administrator", and
until this phase nothing outside `SeedData` wrote either.

## Decision

**A tenant must always have at least one active `TenantAdmin`**, and a write that
would end that is refused with `LastTenantAdmin` (`ErrorKind.RuleViolation` →
422).

### Why it is worth enforcing at all

A tenant with no administrators is not merely awkward, it is unrecoverable
through this API:

- every endpoint that could grant the role back is itself `TenantAdmin`-only;
- there is no SysAdmin rescue path — FR-1.3's platform-operator surface has no
  controller either, and `AuthorizationPolicies.TenantMember` deliberately
  excludes a SysAdmin from tenant-scoped reads anyway (decision `0012`, PRD §2);
- decision `0028` routes approval requests on a gated resource with no assigned
  approvers to `IUserRepository.FindTenantAdminUserIdsAsync`. With no admins,
  such a tenant creates `Pending` bookings that notify **nobody**, and FR-9.3's
  expiry job then decides them unseen — the exact failure `0028`'s
  `NotificationsFor` change was written to prevent.

Recovery would be a database edit.

### It covers both doors, and only the doors

The guard fires when a write would remove the user from the set of *active
TenantAdmins*:

| Current state | Operation | Guarded? |
|---|---|---|
| active, holds the role | deactivate | yes |
| active, holds the role | roles replaced without it | yes |
| active, holds the role | roles replaced keeping it | no — the set does not shrink |
| inactive, holds the role | either | no — not in the set to begin with |
| does not hold the role | either | no |
| anything | reactivate | no — the set can only grow |

Reactivation therefore opens no transaction and takes no lock, and its handler
says so, because ceremony that implies a rule nobody enforces is worse than no
ceremony: the next reader would trust it.

### It is checked under a lock, in the same transaction as the write

This is the part that is easy to get wrong and easy to believe you got right.

The guard is a read-check-write across two rows. Two administrators each
removing the other's role at the same moment both read "there are two", both
pass, and the tenant ends with none. CLAUDE.md §6's own table says a PRD-style
"must never" belongs in tiers 1–3, and tier 2 is the locking protocol.

So `IUserRepository.CountOtherActiveTenantAdminsAsync` is raw SQL with
`UPDLOCK, HOLDLOCK` on **both** `dbo.Users` and `dbo.UserRoles`, and the two
guarded handlers run their whole read-check-write inside
`IUnitOfWork.ExecuteAsync`. `UPDLOCK` stops two transactions holding the read at
once; `HOLDLOCK` keeps it a range lock to commit, so nobody can insert or
reactivate an administrator into the range underneath either of them. Locking
only one table would leave the other free to move.

No stored procedure, unlike the booking path: CLAUDE.md §4.1's
procedure-or-nothing rule is about *booking* writes specifically, and the reason
behind it — that LINQ cannot express the hints — is satisfied here by raw SQL
with a parameterised `FormattableString` (§5).

The count deliberately excludes the user being written, so the question's answer
does not depend on whether this transaction's own change has landed yet: zero
means this write would empty the set.

## Consequences

- **The refusal has to be actionable, and the wording is part of the decision**:
  "give somebody else the administrator role first". `LastTenantAdminException`
  carries it for the log and phase 7's screen renders the client-facing version,
  which is the one that matters — a wall an admin cannot act on would push them
  towards the database edit this rule exists to avoid.
- **An administrator may still step down or deactivate themselves** while
  another active one remains. The rule protects the tenant, not any particular
  person, and adding a self-action rule would be a second invention nothing
  asked for.
- **Both doors report identically** — same status, same code, same body — so a
  client handles one case. Proven by comparing whole responses.
- **`Users` still has no `RowVersion`** (docs/user-management-plan.md §4.5).
  That is a separate gap about two admins overwriting each other's *role edits*
  last-write-wins, and it is unchanged here: this lock serializes the guard, not
  the edit. Flagged rather than fixed, consistent with the replace-the-set
  editors in `admin-plan.md` §4.2.
- **The guard cost a test that was lying.** The first concurrency test fired two
  real HTTP requests at once and passed — and went on passing with the hints
  removed, five runs out of five, because the window between read and write is
  too narrow for two TestServer requests to interleave by luck. It is kept, with
  its comment corrected to say what it actually proves (the endpoint behaves
  under parallelism), and `UserLastAdminGuardConcurrencyTests` was added to
  force the interleaving deterministically: T1 takes the lock and holds it 1.5s
  before writing, T2 starts 300ms later and must block. That one fails 2/2 on
  every run with the hints removed and passes with them — which is what makes it
  evidence.
