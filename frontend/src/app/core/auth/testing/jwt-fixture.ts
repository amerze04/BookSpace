// Test-only: builds a syntactically real JWT string (three dot-separated
// base64url segments) so decodeAccessToken has something genuine to parse,
// without a live backend issuing one. Never imported by production code.
//
// `exp` defaults 15 minutes out (matching decisions/0009's real lifetime) so
// every existing test gets a token decodeAccessToken accepts and the guards/
// interceptor treat as live, without having to pass it explicitly. Pass an
// `exp` in the past to build an expired one for expiry-specific tests.
export function buildFakeAccessToken(claims: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = base64UrlEncode(
    JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 900, ...claims }),
  );
  return `${header}.${payload}.fake-signature`;
}

function base64UrlEncode(value: string): string {
  return btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
