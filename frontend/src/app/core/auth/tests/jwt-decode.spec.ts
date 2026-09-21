import { decodeAccessToken, isAccessTokenExpired } from '../jwt-decode';
import { buildFakeAccessToken } from '../testing/jwt-fixture';

// .NET's ClaimTypes.Role, spelled out in full — the whole point of this file
// is pinning down that the real key is this, not "role" (see jwt-decode.ts).
const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

describe('decodeAccessToken', () => {
  it('reads sub, email and orgId from their short claim names', () => {
    const token = buildFakeAccessToken({
      sub: 'user-1',
      email: 'member1@acme.test',
      orgId: 'org-1',
      [ROLE_CLAIM]: 'Member',
    });

    const claims = decodeAccessToken(token);

    expect(claims.sub).toBe('user-1');
    expect(claims.email).toBe('member1@acme.test');
    expect(claims.orgId).toBe('org-1');
  });

  it('reads a single role as a one-element array', () => {
    const token = buildFakeAccessToken({ sub: 'u', email: 'e', [ROLE_CLAIM]: 'Member' });

    expect(decodeAccessToken(token).roles).toEqual(['Member']);
  });

  it('reads multiple roles as an array under the long ClaimTypes.Role URI', () => {
    const token = buildFakeAccessToken({ sub: 'u', email: 'e', [ROLE_CLAIM]: ['TenantAdmin', 'Approver'] });

    expect(decodeAccessToken(token).roles).toEqual(['TenantAdmin', 'Approver']);
  });

  it('treats a missing orgId as null — a SysAdmin token', () => {
    const token = buildFakeAccessToken({ sub: 'sysadmin-1', email: 'ops@bookspace.internal', [ROLE_CLAIM]: 'SysAdmin' });

    expect(decodeAccessToken(token).orgId).toBeNull();
  });

  it('throws rather than silently returning nonsense for something that is not a JWT', () => {
    expect(() => decodeAccessToken('not-a-jwt')).toThrow();
  });

  it('throws for a token with no exp claim, rather than treating it as never expiring', () => {
    const token = buildFakeAccessToken({ sub: 'u', email: 'e', [ROLE_CLAIM]: 'Member', exp: undefined });

    expect(() => decodeAccessToken(token)).toThrow();
  });
});

describe('isAccessTokenExpired', () => {
  it('is false for a token whose exp is in the future', () => {
    const token = buildFakeAccessToken({ sub: 'u', email: 'e', [ROLE_CLAIM]: 'Member' });

    expect(isAccessTokenExpired(decodeAccessToken(token))).toBe(false);
  });

  it('is true for a token whose exp is in the past', () => {
    const token = buildFakeAccessToken({
      sub: 'u',
      email: 'e',
      [ROLE_CLAIM]: 'Member',
      exp: Math.floor(Date.now() / 1000) - 60,
    });

    expect(isAccessTokenExpired(decodeAccessToken(token))).toBe(true);
  });

  it('is true exactly at the expiry instant, not just after it', () => {
    const claims = decodeAccessToken(buildFakeAccessToken({ sub: 'u', email: 'e', [ROLE_CLAIM]: 'Member' }));

    expect(isAccessTokenExpired(claims, claims.exp * 1000)).toBe(true);
  });
});
