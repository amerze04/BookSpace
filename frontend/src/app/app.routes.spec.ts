import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { buildFakeAccessToken } from './core/auth/testing/jwt-fixture';

// Item 3: the shell route (path: '') guards entry with canActivate, but that
// alone never re-runs on navigation between its own already-loaded children —
// canActivateChild is what has to catch a session going bad, or an access
// token expiring, while the visitor stays inside the shell. These tests drive
// the real app.routes.ts config end to end rather than the guard function in
// isolation, so a regression in the routing wiring itself (not just the guard
// logic already covered by auth.guard.spec.ts) would be caught here.

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const API = 'http://localhost:5270';

function seedSession(expUnixSeconds?: number): void {
  const token = buildFakeAccessToken({
    sub: 'u1',
    email: 'a@acme.test',
    orgId: 'org-1',
    [ROLE_CLAIM]: 'Member',
    ...(expUnixSeconds !== undefined ? { exp: expUnixSeconds } : {}),
  });
  localStorage.setItem('bookspace.accessToken', token);
  localStorage.setItem('bookspace.refreshToken', 'refresh-1');
}

describe('shell route guarding (canActivateChild)', () => {
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('lets a still-valid session move between two already-loaded shell children, with no network call', async () => {
    seedSession();
    const harness = await RouterTestingHarness.create('/home');

    await harness.navigateByUrl('/settings');

    expect(router.url).toBe('/settings');
    httpMock.expectNone(`${API}/auth/refresh`);
  });

  it('transparently refreshes an expired access token on a child-to-child navigation', async () => {
    vi.useFakeTimers();
    try {
      // A short-lived token, still the very same one throughout — its claims
      // never get rewritten, only real (simulated) time passes underneath it,
      // exactly like a genuinely expiring access token would.
      seedSession(Math.floor(Date.now() / 1000) + 30);
      const harness = await RouterTestingHarness.create('/home');

      vi.setSystemTime(Date.now() + 60_000);

      const navigation = harness.navigateByUrl('/settings');
      await vi.advanceTimersByTimeAsync(0);

      const newAccessToken = buildFakeAccessToken({ sub: 'u1', email: 'a@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      httpMock
        .expectOne(`${API}/auth/refresh`)
        .flush({ accessToken: newAccessToken, expiresIn: 900, refreshToken: 'refresh-2' });

      await navigation;

      expect(router.url).toBe('/settings');
    } finally {
      vi.useRealTimers();
    }
  });

  it('redirects to /login when the session goes terminally invalid while already inside the shell', async () => {
    vi.useFakeTimers();
    try {
      seedSession(Math.floor(Date.now() / 1000) + 30);
      const harness = await RouterTestingHarness.create('/home');

      // Time passes and the access token expires; the refresh token is now
      // rejected too — as if it had already been revoked (decisions/0011's
      // reuse-detection, or simply past its own absolute expiry).
      vi.setSystemTime(Date.now() + 60_000);

      const navigation = harness.navigateByUrl('/settings');
      await vi.advanceTimersByTimeAsync(0);

      httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 401, statusText: 'Unauthorized' });

      await navigation;

      expect(router.url).toBe('/login?returnUrl=%2Fsettings');
    } finally {
      vi.useRealTimers();
    }
  });

  it('retains the approver-specific guard on top of canActivateChild', async () => {
    seedSession();
    const harness = await RouterTestingHarness.create('/home');

    await harness.navigateByUrl('/approvals');

    // Member is not an eligible approver (decision 0018) — approverGuard
    // still redirects even though canActivateChild's own session check passed.
    expect(router.url).toBe('/home');
  });
});
