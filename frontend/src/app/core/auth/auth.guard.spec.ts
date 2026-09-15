import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { firstValueFrom, isObservable } from 'rxjs';
import { approverGuard, authGuard, guestOnlyGuard } from './auth.guard';
import { buildFakeAccessToken } from './testing/jwt-fixture';

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

  it('approverGuard redirects a Member to /home', () => {
    seedSession('Member');
    const blocked = TestBed.runInInjectionContext(() => approverGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(router.serializeUrl(blocked as UrlTree)).toBe('/home');
  });
});
