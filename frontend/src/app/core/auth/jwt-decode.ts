// Decodes only — this never verifies the signature. The backend already
// verified it; the client is just reading the claims it was handed. Never
// treat anything read here as an authorization decision (CLAUDE.md, WP-6
// plan §2) — the backend enforces every real permission.
export interface DecodedAccessToken {
  sub: string;
  email: string;
  orgId: string | null;
  roles: string[];
  // Seconds since epoch (standard JWT `exp`), not milliseconds. Every access
  // token the backend issues carries one (decisions/0009, 15-minute lifetime)
  // — treated as required here rather than optional, because a client that
  // silently accepted a token with no `exp` would never know to refresh it.
  exp: number;
}

// .NET's JwtSecurityToken (built directly from a Claim list, not through
// JwtSecurityTokenHandler's short-name mapping) writes ClaimTypes.Role's full
// URI as the JSON key rather than "role" — confirmed against a real token
// from POST /auth/login. Easy to get wrong by going on the claim's short name
// alone (decisions/0009's table calls it "role" for readability).
const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

export function decodeAccessToken(accessToken: string): DecodedAccessToken {
  const payloadSegment = accessToken.split('.')[1];
  if (!payloadSegment) {
    throw new Error('Not a JWT: missing payload segment.');
  }

  const payload = JSON.parse(base64UrlDecode(payloadSegment)) as Record<string, unknown>;

  const roleClaim = payload[ROLE_CLAIM];
  const roles = Array.isArray(roleClaim) ? (roleClaim as string[]) : roleClaim ? [roleClaim as string] : [];

  const exp = payload['exp'];
  if (typeof exp !== 'number') {
    throw new Error('Not a valid access token: missing or non-numeric exp claim.');
  }

  return {
    sub: payload['sub'] as string,
    email: payload['email'] as string,
    orgId: (payload['orgId'] as string | undefined) ?? null,
    roles,
    exp,
  };
}

// exp is in seconds; Date.now() is in milliseconds. No clock-skew leeway —
// the access token's own 15-minute lifetime (decisions/0009) makes a few
// seconds of drift immaterial, and the interceptor already retries on a real
// 401 if this ever calls it valid a moment too late.
export function isAccessTokenExpired(claims: DecodedAccessToken, nowMs: number = Date.now()): boolean {
  return nowMs >= claims.exp * 1000;
}

// JWTs use base64url (RFC 4648 §5): '-'/'_' instead of '+'/'/', and the '='
// padding stripped. atob() only understands plain base64, so both have to be
// undone before it can decode the segment.
function base64UrlDecode(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  return atob(base64);
}
