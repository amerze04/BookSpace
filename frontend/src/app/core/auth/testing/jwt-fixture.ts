// Test-only: builds a syntactically real JWT string (three dot-separated
// base64url segments) so decodeAccessToken has something genuine to parse,
// without a live backend issuing one. Never imported by production code.
export function buildFakeAccessToken(claims: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = base64UrlEncode(JSON.stringify(claims));
  return `${header}.${payload}.fake-signature`;
}

function base64UrlEncode(value: string): string {
  return btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
