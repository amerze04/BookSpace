import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { approverGuard, authGuard, guestOnlyGuard } from './auth.guard';
import { buildFakeAccessToken } from './testing/jwt-fixture';

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

function seedSession(roles: string | string[]): void {
  const token = buildFakeAccessToken({ sub: 'u1', email: 'a@acme.test', orgId: 'org-1', [ROLE_CLAIM]: roles });
  localStorage.setItem('bookspace.accessToken', token);
  localStorage.setItem('bookspace.refreshToken', 'refresh-1');
}

describe('route guards', () => {
  let router: Router;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    router = TestBed.inject(Router);
  });

  afterEach(() => localStorage.clear());

  it('authGuard redirects an unauthenticated visitor to /login, remembering where they were headed', () => {
    const result = TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url: '/resources' } as RouterStateSnapshot),
    );

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login?returnUrl=%2Fresources');
  });

  it('authGuard lets an authenticated visitor through', () => {
    seedSession('Member');

    const result = TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url: '/resources' } as RouterStateSnapshot),
    );

    expect(result).toBe(true);
  });

  it('guestOnlyGuard keeps an authenticated visitor off the login page', () => {
    seedSession('Member');

    const result = TestBed.runInInjectionContext(() => guestOnlyGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));

    expect(router.serializeUrl(result as UrlTree)).toBe('/');
  });

  it('guestOnlyGuard admits an unauthenticated visitor', () => {
    const result = TestBed.runInInjectionContext(() => guestOnlyGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));

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
