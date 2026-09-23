import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../app.routes';
import { buildFakeAccessToken } from '../core/auth/testing/jwt-fixture';

// Item 3: the shell route (path: '') guards entry with canActivate, but that
// alone never re-runs on navigation between its own already-loaded children —
// canActivateChild is what has to catch a session going bad, or an access
// token expiring, while the visitor stays inside the shell. These tests drive
// the real app.routes.ts config end to end rather than the guard function in
// isolation, so a regression in the routing wiring itself (not just the guard
// logic already covered by auth.guard.spec.ts) would be caught here.

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const API = 'http://localhost:5270';

// `roles` gained a parameter in admin console phase 2 (it was always 'Member')
// so the /admin route tree can be driven from both sides. Defaulted, so every
// pre-existing caller means exactly what it used to.
function seedSession(expUnixSeconds?: number, roles: string | string[] = 'Member'): void {
  const token = buildFakeAccessToken({
    sub: 'u1',
    email: 'a@acme.test',
    orgId: 'org-1',
    [ROLE_CLAIM]: roles,
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

  // These navigate between two *placeholder* children on purpose. /home used to
  // serve as the neutral "some authenticated page" here, but since WP-7 Phase 4
  // it redirects to /calendar, which immediately fetches its own date window —
  // leaving an open request that httpMock.verify() rightly objects to, and
  // (worse) corrupting the shared TestBed for every spec file after this one.
  // What these tests are actually about is the guard wiring, not any particular
  // screen, so they use routes that fetch nothing.
  it('lets a still-valid session move between two already-loaded shell children, with no network call', async () => {
    seedSession();
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/help');

    expect(router.url).toBe('/help');
    httpMock.expectNone(`${API}/auth/refresh`);
  });

  it('transparently refreshes an expired access token on a child-to-child navigation', async () => {
    vi.useFakeTimers();
    try {
      // A short-lived token, still the very same one throughout — its claims
      // never get rewritten, only real (simulated) time passes underneath it,
      // exactly like a genuinely expiring access token would.
      seedSession(Math.floor(Date.now() / 1000) + 30);
      const harness = await RouterTestingHarness.create('/settings');

      vi.setSystemTime(Date.now() + 60_000);

      const navigation = harness.navigateByUrl('/help');
      await vi.advanceTimersByTimeAsync(0);

      const newAccessToken = buildFakeAccessToken({ sub: 'u1', email: 'a@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      httpMock
        .expectOne(`${API}/auth/refresh`)
        .flush({ accessToken: newAccessToken, expiresIn: 900, refreshToken: 'refresh-2' });

      await navigation;

      expect(router.url).toBe('/help');
    } finally {
      vi.useRealTimers();
    }
  });

  it('redirects to /login when the session goes terminally invalid while already inside the shell', async () => {
    vi.useFakeTimers();
    try {
      seedSession(Math.floor(Date.now() / 1000) + 30);
      const harness = await RouterTestingHarness.create('/settings');

      // Time passes and the access token expires; the refresh token is now
      // rejected too — as if it had already been revoked (decisions/0011's
      // reuse-detection, or simply past its own absolute expiry).
      vi.setSystemTime(Date.now() + 60_000);

      const navigation = harness.navigateByUrl('/help');
      await vi.advanceTimersByTimeAsync(0);

      httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 401, statusText: 'Unauthorized' });

      await navigation;

      expect(router.url).toBe('/login?returnUrl=%2Fhelp');
    } finally {
      vi.useRealTimers();
    }
  });

  it('retains the approver-specific guard on top of canActivateChild', async () => {
    seedSession();
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/approvals');

    // Member is not an eligible approver (decision 0018) — approverGuard
    // still redirects even though canActivateChild's own session check passed.
    // The landing screen is /calendar since WP-7 Phase 4, so the redirect
    // genuinely renders the calendar and its own window fetch has to be
    // answered here rather than left open.
    expect(router.url).toBe('/calendar');
    flushCalendarWindow();
  });

  // The two redirects the WP-7 Phase 4 re-plan introduced. Worth their own
  // tests rather than being implied by the ones above: /home was the landing
  // route for two work packages, so a bookmark pointing at it has to keep
  // working instead of falling through to the catch-all and bouncing the
  // visitor to /login.
  it('redirects /home to the calendar', async () => {
    seedSession();
    await RouterTestingHarness.create('/home');

    expect(router.url).toBe('/calendar');
    flushCalendarWindow();
  });

  it('redirects the app root to the calendar', async () => {
    seedSession();
    await RouterTestingHarness.create('/');

    expect(router.url).toBe('/calendar');
    flushCalendarWindow();
  });

  // ---- /admin (admin console phase 2) ----

  // The guard sits on the `admin` parent, not on each child, so every screen
  // phases 3-6 add is covered without remembering to ask. This drives the real
  // route config to prove that placement actually protects a *child* path —
  // which a guard on the children alone would also do, and a guard on the
  // parent alone would not if canActivate were the wrong hook.
  it('keeps a Member out of an /admin child route', async () => {
    seedSession();
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/admin/resources');

    expect(router.url).toBe('/calendar');
    flushCalendarWindow();
  });

  // An Approver reaches /approvals and must not reach /admin. The two guards
  // are genuinely different rules, and this is the navigation that tells them
  // apart.
  it('keeps an Approver out of /admin, even though /approvals admits them', async () => {
    seedSession(undefined, 'Approver');
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/admin/resources');

    expect(router.url).toBe('/calendar');
    flushCalendarWindow();
  });

  it('lets a TenantAdmin into /admin/resources', async () => {
    seedSession(undefined, 'TenantAdmin');
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/admin/resources');

    expect(router.url).toBe('/admin/resources');
  });

  // /admin itself is a redirect, so a typed or bookmarked bare /admin lands
  // somewhere real rather than on an empty outlet.
  it('redirects a bare /admin to the resources list', async () => {
    seedSession(undefined, 'TenantAdmin');
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/admin');

    expect(router.url).toBe('/admin/resources');
  });

  // The redirect must not become a way around the guard: a Member asking for
  // the parent is refused before the child is ever resolved.
  it('refuses a Member at the bare /admin redirect too', async () => {
    seedSession();
    const harness = await RouterTestingHarness.create('/settings');

    await harness.navigateByUrl('/admin');

    expect(router.url).toBe('/calendar');
    flushCalendarWindow();
  });

  // The calendar fetches its visible window as soon as it renders; these tests
  // are about routing, so the response is answered and discarded rather than
  // asserted on (calendar.component.spec.ts owns what the request looks like).
  function flushCalendarWindow(): void {
    httpMock
      .expectOne((r) => r.url === `${API}/bookings`)
      .flush({
        items: [],
        page: 1,
        pageSize: 100,
        totalCount: 0,
        totalPages: 0,
        hasPreviousPage: false,
        hasNextPage: false,
      });
  }
});
