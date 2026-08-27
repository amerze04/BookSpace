# 0010 — Email addresses are globally unique

**Status:** Decided (2026-08-26) — **decided by the repo owner**, not the
assistant
**Requirements:** FR-1.5, FR-2.1
**Raised by:** WP-2 Phase 3. Login needs to identify a user from an email, and
the WP-1 schema did not guarantee that an email identified only one.

## The problem

WP-1 gave `Users` this index:

```sql
CREATE UNIQUE INDEX UX_Users_Org_Email
    ON Users (OrgId, Email)
    WHERE OrgId IS NOT NULL;
```

Two consequences, both surfaced when designing login:

1. **An email could exist in more than one tenant.** `alice@example.com` in Acme
   and `alice@example.com` in Globex were both legal. So `POST /auth/login` with
   an email and a password could not identify a single user — it would have
   needed a tenant discriminator (an org slug in the body, a subdomain, a
   header).
2. **SysAdmin rows had no email uniqueness at all.** The filter excludes
   `OrgId IS NULL`, so two platform operators could share an email address —
   on the most privileged accounts in the system.

## Decision

An email address identifies exactly one user across the whole platform.

```sql
CREATE UNIQUE INDEX UQ_Users_Email ON Users (Email);   -- unfiltered
```

- Login takes `{ email, password }` with **no tenant discriminator**.
- Emails are normalized (trimmed, lowercased) in the `User` constructor.
- Applied in a new migration; `InitialCreate` is untouched (`CLAUDE.md` §5).

## Why

The alternative was keeping per-tenant emails and adding a discriminator to
login. That was rejected for three reasons:

- **It complicates every login.** The user has to know their tenant slug, or the
  app has to be deployed per-subdomain, before anyone can sign in.
- **It doesn't match the model.** FR-1.5 already says a user belongs to exactly
  one tenant. If one person needs access to two tenants, that is two accounts
  either way — so allowing one email to name two of them buys nothing except
  ambiguity at the point of authentication.
- **The SysAdmin gap has to be closed regardless.** Any fix for that means an
  unfiltered constraint on `Email`, at which case the per-tenant version is
  redundant.

**Why normalize in the domain constructor rather than rely on collation.** SQL
Server's default collation (`SQL_Latin1_General_CP1_CI_AS`) is
case-insensitive, so the unique index would already treat `Alice@x.com` and
`alice@x.com` as the same. But that is a property of how the server happens to
be configured, not of the model — restore the database onto a case-sensitive
instance and the guarantee silently disappears. Normalizing on write makes it
ours. `User.NormalizeEmail` is public so the login path normalizes the submitted
address through the same function.

## Consequences

- The migration **fails loudly** on any database already holding duplicate
  emails across tenants. The seed data uses per-tenant domains
  (`member1@acme.test`, `member1@globex.test`), so it is clean, but a dev
  database with hand-added rows may not be. Failing is the correct behavior —
  a silently skipped unique index is worse than a blocked migration.
- One person needing access to two tenants needs two email addresses. Accepted:
  v1 has no cross-tenant users, and PRD §12 defers SSO where this would
  otherwise come up.
- Proved by `UserEmailUniquenessTests` against real SQL Server — a unique index
  is precisely the behavior the in-memory provider does not have
  (`CLAUDE.md` §8).

## Notes

`docs/bookspace-schema-v2.sql` is updated to match, since it is the declared
source of truth for the data model.
