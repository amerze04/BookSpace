# 0009 — JWT claims shape and token lifetimes

**Status:** Decided (2026-08-26)
**Requirements:** FR-2.1, FR-1.4, FR-1.5
**Raised by:** WP-2 Phase 3. Listed as "still open" in `docs/wp2-plan.md`; this
closes it.

## Decision

Access tokens are JWTs signed with HMAC-SHA256, carrying:

| Claim | Value |
|---|---|
| `sub` | `Users.Id` |
| `email` | `Users.Email` |
| `orgId` | `Users.OrgId` — **omitted entirely** when null |
| `role` | One claim per role (`ClaimTypes.Role`) |
| `jti`, `iat`, `exp`, `nbf`, `iss`, `aud` | Standard registered claims |

- **Access token: 15 minutes. Refresh token: 14 days, absolute.**
- Signing key from configuration only, minimum 32 bytes, validated at startup.
- Validation checks issuer, audience, signing key, and lifetime, with
  `ClockSkew = TimeSpan.Zero` and `MapInboundClaims = false`.

## Why

**`orgId` absent rather than empty for a SysAdmin.** A SysAdmin sits above all
tenants and has `Users.OrgId IS NULL`. The alternatives were an empty string or
`Guid.Empty`, both of which are values the Phase 4 tenant accessor could mistake
for a real tenant — and a tenant-id comparison that accidentally matches is
exactly the isolation failure FR-1.2 exists to prevent. An absent claim cannot
be misread; code has to handle "no tenant" explicitly.

The corollary is a consistency rule: a principal with no `orgId` and no
`SysAdmin` role is malformed and must be rejected, not treated as unscoped. The
`TenantMember` policy enforces this by requiring the claim's presence.

**One `role` claim per role, not a delimited list.** FR-1.5 makes roles additive
within a tenant, and `ClaimTypes.Role` is what ASP.NET Core's `RequireRole` and
`[Authorize(Roles = ...)]` already read — so no claims transformation is needed.

**HMAC-SHA256 rather than RSA/ECDSA.** There is one issuer and one consumer
(this API validates the tokens it issues). Asymmetric keys buy the ability for a
third party to validate without the signing secret, which nothing here needs, at
the cost of key management and a JWKS endpoint. Revisit if a second service ever
needs to validate BookSpace tokens.

**15 minutes / 14 days.** Short enough that a leaked access token has a small
window, long enough not to force a refresh mid-interaction. The refresh window
is absolute rather than sliding — a rotated token inherits the original expiry
instead of restarting the clock — so a stolen token cannot be renewed
indefinitely by an attacker who keeps using it.

**`ClockSkew = TimeSpan.Zero`.** The default is five minutes, which would make a
15-minute token valid for 20. If we say short-lived, it should be short-lived.

**`MapInboundClaims = false`.** Otherwise the handler silently rewrites `sub` to
the long `ClaimTypes.NameIdentifier` URI on read, and what comes back out does
not match what was issued. Phase 4 reads these claims; the shape should be one
thing, not two.

**Signing key never committed** (`CLAUDE.md` §4.4). `appsettings.json` holds
`Issuer`, `Audience`, and the two lifetimes; the key comes from user-secrets in
development and `Jwt__SigningKey` elsewhere. `JwtOptions` is validated with
`ValidateOnStart()`, so a missing or under-32-byte key fails the boot rather
than letting the app serve forgeable tokens.

## Consequences

- Phase 4's tenant accessor reads `orgId` and must treat its absence as
  "SysAdmin, no tenant scope" — never as a tenant.
- The claim shape is now a contract. `JwtAccessTokenServiceTests` and
  `AuthorizationPolicyTests` pin it, the latter through a real validated round
  trip.
- Anyone cloning the repo must set `Jwt:SigningKey` before the API will start.
  This is deliberate — a default development key in source is how a default
  development key reaches production.

## Notes

Not decided here: whether access tokens should ever be revocable before expiry.
They are not, which is the normal trade for stateless validation — 15 minutes is
the exposure window. FR-2.4 is met at the refresh boundary instead
(`0011`).
