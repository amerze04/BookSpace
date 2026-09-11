# 0011 — Refresh token hashing, rotation, and reuse detection

**Status:** Decided (2026-08-26)
**Requirements:** FR-2.1, FR-2.2, FR-2.3, FR-2.4
**Raised by:** WP-2 Phase 3.

## Decision

**Generation.** 32 bytes from `RandomNumberGenerator`, Base64Url-encoded. Handed
to the client once; never stored.

**Storage.** SHA-256 of the raw token, Base64-encoded, in
`RefreshTokens.TokenHash`.

**Rotation.** Every successful refresh revokes the presented token, records
`ReplacedByTokenId`, and issues a replacement in the **same `FamilyId`**. The
replacement **inherits the original `ExpiresAtUtc`** rather than starting a new
window.

**The full decision table:**

| Presented token | Response | Revokes |
|---|---|---|
| Hash not found | 401 `InvalidRefreshToken` | nothing |
| Active, unexpired | 200, new pair | the presented token, replaced |
| Expired, not revoked | 401 `RefreshTokenExpired` | that token only |
| **Already revoked** | 401 `RefreshTokenReuseDetected` | **the whole family** |
| User inactive / org suspended | 401 `AccountInactive` | the whole family |

**Logout** revokes the whole family and returns 204 whether or not the token was
recognized.

## Why

### SHA-256 for refresh tokens, PBKDF2 for passwords

This looks inconsistent in the same codebase, so the reasoning matters:

1. **Lookup is by hash.** A presented refresh token is found by hashing it and
   matching `UQ_RefreshTokens_TokenHash`. PBKDF2 salts per call, so the same
   token would produce a different hash every time and the index would be
   unusable — the only alternative being to scan every row and verify each one.
2. **The threat model is different.** A password is low-entropy, human-chosen,
   and often reused, so an attacker with the hash file can attack it offline
   with a dictionary — which is exactly what a deliberately slow KDF defeats. A
   refresh token is 256 bits from a CSPRNG. There is no dictionary and no
   guessing; the cost factor protects against nothing.

Hashing at all still matters: FR-2.3 means a database leak must not hand the
attacker usable tokens, and SHA-256 achieves that. Passwords use
`PasswordHasher<T>` (PBKDF2-HMAC-SHA512, 100k iterations) for the opposite
reasons.

### Why reuse kills the family and expiry does not

A revoked token being presented means two parties hold it: the legitimate client
already rotated it, so whoever just sent it has a copy they should not. The
system cannot tell which of the two is the attacker, so the only safe move is to
end the session for both and force a fresh login (FR-2.2). Killing the family —
rather than just refusing the request — is what makes theft self-limiting.

Expiry is not evidence of anything. It is what happens to every token
eventually, so it costs the one token and leaves the family alone.

Revocation is scoped to the family, not to the user, so one compromised session
does not sign the user out of their other devices. `IX_RefreshTokens_Family`
exists for this query.

### Absolute rather than sliding window

If a rotated token got a fresh 14 days, an attacker who kept refreshing would
hold a session forever. Inheriting the original expiry caps the family at 14
days from login regardless of how often it rotates.

### The concurrent-refresh race

Two requests presenting the same valid token could both read it as active and
both mint a replacement. `RefreshToken.RevokedAtUtc` is mapped as a **concurrency
token**, so EF appends `AND RevokedAtUtc IS NULL` to the revoking UPDATE; the
loser affects zero rows and gets `DbUpdateConcurrencyException`, which
`GlobalExceptionHandler` already maps to 409. This needs no new column and no
DDL — model metadata only.

Rotation is one `SaveChangesAsync` (revoke + insert together), so EF's implicit
transaction covers it and `EnableRetryOnFailure` can retry the pair. Nothing
here calls `BeginTransaction`, so `CreateExecutionStrategy()` is not yet
required (`CLAUDE.md` §5) — but it will be the moment any auth operation needs
two round trips.

### Uniform failure reporting on login

Unknown email, wrong password, deactivated user, and suspended organization all
return the same `InvalidCredentials` response. Distinguishing them would make
login an account-enumeration oracle. The real cause goes to the log, where the
correlation ID makes it traceable.

### Refresh token in the response body, not an httpOnly cookie

Both sides, since this will be asked:

- **Cookie:** immune to XSS token theft, since script cannot read it. Costs CSRF
  protection, a `SameSite`/domain story, and a login flow the API can no longer
  be tested against with plain HTTP.
- **Body:** simple, testable, framework-agnostic. Exposed to XSS if the SPA has
  an XSS hole.

Chosen: body, because the Angular app does not exist yet (M4) and picking a
cookie strategy now would be designing for a client whose hosting model is
undecided. Worth revisiting at M4 — it is a change to two handlers and the
controller, not to the token model.

**Revisited 2026-09-11, at WP-6 (the Angular app landing).** Answer unchanged:
stays body-based, client-held. An httpOnly cookie is still the more secure
option against XSS, but adopting it now would mean backend work
(`AllowCredentials`, `Set-Cookie` on three endpoints, a CSRF story) inside a
work package scoped as frontend-only (`docs/wp6-plan.md` §3). Nothing about
the trade-off itself has changed — this just records that the revisit
happened and the owner's call was "not this week."

## Consequences

- `FR-2.4` is enforced at the refresh boundary: a deactivated user or suspended
  organization loses the session on the next refresh, at most 15 minutes after
  the change. Immediate revocation would require checking the database on every
  request, which is the cost stateless access tokens exist to avoid.
- Logout is silent about unknown tokens, so it cannot be used to probe which
  tokens are live.
- Proved by `RefreshTokenCommandHandlerTests` (one test per row of the table
  above) and re-proved end to end over HTTP in `AuthenticationEndpointTests`.
