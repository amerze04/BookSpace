# 0030 — What `POST /users` may say when an email address is taken

## Status
Decided and implemented 2026-09-24, user management phase 3.

## Context

Decision `0010` made email identify exactly one user **platform-wide** —
`UQ_Users_Email` is global and unfiltered, with no tenant discriminator — and
settled that for *login*, where it removes the need to ask which organization a
person belongs to.

It never covered what a **creation refusal** may reveal. A TenantAdmin creating a
colleague can collide with an address in their own tenant, which they can see, or
with one in an organization they cannot see and have no business knowing exists.

## Decision

**One code, one message, one response, either way**: `EmailAlreadyInUse`,
`ErrorKind.Conflict` → 409, with copy that does not say which tenant holds the
address and a message that does not name the address.

Answering differently in the two cases — a different code, a different message,
or merely a 409 in one case and a 201 in the other — turns `POST /users` into a
cross-tenant existence oracle. An administrator could enumerate addresses across
the platform one create at a time. That is the disclosure AC-4 forbids, and it is
the concern decision `0018` took seriously enough to collapse three distinct
approver-ineligibility reasons into a single code.

**The refusal is raised by the unique index, not by a pre-check, and that is part
of the decision rather than an implementation detail.** A pre-check able to see
another tenant's row would have to be an unfiltered read, and CLAUDE.md §4.2
reserves that surface to the two named methods on
`IAuthenticationUserRepository`. Letting `UQ_Users_Email` answer means the code
raising the refusal **cannot** learn which tenant the collision is in, rather
than merely declining to say — the property is structural, not a matter of a
future maintainer's discipline.

`UserRepository.SaveChangesAsync` translates SQL error 2601/2627 into
`EmailAlreadyInUseException`. It checks the **index name** as well as the error
number, because `Users` also carries `UQ_Users_CalendarFeedToken` and
`UQ_Users_Org_Id`; reporting either of those as "that email is taken" would send
an administrator hunting a problem that is not there.

## Consequences

- **Conflict, not Validation.** The address is well-formed; what refuses it is
  the current state of the data, which is also why it may succeed later — after
  a typo is corrected, or the other account is renamed.
- **`EmailAlreadyInUseException` carries nothing** — no address, no tenant, no
  `Extensions`, and nothing in its log message either. That is deliberately
  unlike `ApproverNotEligibleException` next door, which *does* log the offending
  ids: those came from the caller's own request body and named their own tenant's
  users. Here the interesting fact — whose account already holds the address — is
  precisely the one nothing on this path may learn, and the address the caller
  sent is already in the request log.
- **An administrator cannot tell a same-tenant collision from a cross-tenant
  one, and that costs them something real.** "Is this person already in my
  organization?" is a reasonable question with an unhelpful answer. Accepted:
  phase 4's directory read gives them a way to look, which is the right place for
  it, and the alternative leaks across tenants on every create.
- **Case cannot be used to slip past it.** `User.NormalizeEmail` lower-cases and
  trims on the way in, so `MEMBER1@ACME.TEST` collides with `member1@acme.test`.
  Proven against the running API, not inferred.
- **The rule is proven where it can be**: an integration test creates against an
  address in the caller's own tenant and one belonging to another tenant, and
  compares the whole HTTP response bodies minus `correlationId` and `traceId`.
  A unit test cannot show this — a fake has only one tenant to collide in.
