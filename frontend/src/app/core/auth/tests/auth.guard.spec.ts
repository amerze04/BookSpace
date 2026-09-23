import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { firstValueFrom, isObservable } from 'rxjs';
import { adminGuard, approverGuard, authGuard, guestOnlyGuard } from '../auth.guard';
import { buildFakeAccessToken } from '../testing/jwt-fixture';

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const API = 'http://localhost:5270';

function seedSession(roles: string | string[], expUnixSeconds?: number): void {
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

// authGuard/guestOnlyGuard are async now (item 7 — a stored-but-expired token
// must not be accepted outright, it must be refreshed first), so every result
// here is resolved through this rather than compared directly. Neither guard
// ever actually returns a RedirectCommand (only CanActivateFn's own type
// allows it) — the cast reflects what these two guards actually produce.
async function resolve(result: unknown): Promise<boolean | UrlTree> {
  if (isObservable(result)) {
    return firstValueFrom(result) as Promise<boolean | UrlTree>;
  }
  return result as boolean | UrlTree;
}

describe('route guards', () => {
  let router: Router;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    router = TestBed.inject(Router);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('authGuard redirects an unauthenticated visitor to /login, remembering where they were headed', async () => {
    const result = await resolve(
      TestBed.runInInjectionContext(() =>
        authGuard({} as ActivatedRouteSnapshot, { url: '/resources' } as RouterStateSnapshot),
      ),
    );

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login?returnUrl=%2Fresources');
  });

  it('authGuard lets an authenticated visitor with a live access token through, with no network call', async () => {
    seedSession('Member');

    const result = await resolve(
      TestBed.runInInjectionContext(() =>
        authGuard({} as ActivatedRouteSnapshot, { url: '/resources' } as RouterStateSnapshot),
      ),
    );

    expect(result).toBe(true);
    httpMock.expectNone(`${API}/auth/refresh`);
  });

  it('authGuard refreshes an expired access token and lets the visitor through on success', async () => {
    seedSession('Member', Math.floor(Date.now() / 1000) - 60);

    const resultPromise = resolve(
      TestBed.runInInjectionContext(() =>
        authGuard({} as ActivatedRouteSnapshot, { url: '/resources' } as RouterStateSnapshot),
      ),
    );

    const newAccessToken = buildFakeAccessToken({ sub: 'u1', email: 'a@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    httpMock
      .expectOne(`${API}/auth/refresh`)
      .flush({ accessToken: newAccessToken, expiresIn: 900, refreshToken: 'refresh-2' });

    expect(await resultPromise).toBe(true);
  });

  it('authGuard sends a visitor with an expired token and a dead refresh token to /login', async () => {
    seedSession('Member', Math.floor(Date.now() / 1000) - 60);

    const resultPromise = resolve(
      TestBed.runInInjectionContext(() =>
        authGuard({} as ActivatedRouteSnapshot, { url: '/resources' } as RouterStateSnapshot),
      ),
    );

    httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 401, statusText: 'Unauthorized' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login?returnUrl=%2Fresources');
  });

  it('guestOnlyGuard keeps an authenticated visitor off the login page', async () => {
    seedSession('Member');

    const result = await resolve(
      TestBed.runInInjectionContext(() => guestOnlyGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)),
    );

    expect(router.serializeUrl(result as UrlTree)).toBe('/');
  });

  it('guestOnlyGuard admits an unauthenticated visitor', async () => {
    const result = await resolve(
      TestBed.runInInjectionContext(() => guestOnlyGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)),
    );

    expect(result).toBe(true);
  });

  it('approverGuard admits an Approver', () => {
    seedSession('Approver');
    expect(TestBed.runInInjectionContext(() => approverGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe(true);
  });

  it('approverGuard admits a TenantAdmin', () => {
    seedSession('TenantAdmin');
    expect(TestBed.runInInjectionContext(() => approverGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe(true);
  });

  // /calendar since WP-7 Phase 4 — the app's landing screen. Asserted against
  // the real destination rather than /home, which is now itself only a
  // redirect: bouncing through one would make the URL the visitor lands on
  // differ from the one the guard names.
  it('approverGuard redirects a Member to /calendar', () => {
    seedSession('Member');
    const blocked = TestBed.runInInjectionContext(() => approverGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(router.serializeUrl(blocked as UrlTree)).toBe('/calendar');
  });

  // ---- adminGuard (admin console phase 2) ----

  it('adminGuard admits a TenantAdmin', () => {
    seedSession('TenantAdmin');
    expect(TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe(true);
  });

  // The difference from approverGuard, and the reason both exist: an Approver
  // reaches /approvals and must not reach /admin. A single "elevated" guard
  // would have conflated the two.
  it('adminGuard redirects an Approver to /calendar', () => {
    seedSession('Approver');
    const blocked = TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(router.serializeUrl(blocked as UrlTree)).toBe('/calendar');
  });

  it('adminGuard redirects a Member to /calendar', () => {
    seedSession('Member');
    const blocked = TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(router.serializeUrl(blocked as UrlTree)).toBe('/calendar');
  });

  // **The one that is easy to get wrong by mirroring the backend's policy
  // name.** AuthorizationPolicies.TenantAdmin admits SysAdmin by role, so a
  // guard written from the policy would let them in — onto a console where
  // every request answers 403, because every admin endpoint also requires the
  // orgId claim a SysAdmin does not have (decisions/0009, PRD §2).
  it('adminGuard redirects a SysAdmin to /calendar, despite the backend policy admitting the role', () => {
    seedSession('SysAdmin');
    const blocked = TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(router.serializeUrl(blocked as UrlTree)).toBe('/calendar');
  });

  it('adminGuard admits a TenantAdmin who also holds Approver', () => {
    seedSession(['Approver', 'TenantAdmin']);
    expect(TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe(true);
  });

  it('adminGuard redirects a visitor with no session at all', () => {
    const blocked = TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(router.serializeUrl(blocked as UrlTree)).toBe('/calendar');
  });
});
