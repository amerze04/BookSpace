import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { SKIP_ERROR_TOAST } from '../http/skip-error-toast';
import { buildFakeAccessToken } from './testing/jwt-fixture';

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const API = 'http://localhost:5270';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('stores both tokens and decodes claims on a successful login, without triggering the global error toast', async () => {
    const accessToken = buildFakeAccessToken({
      sub: 'u1',
      email: 'member1@acme.test',
      orgId: 'org-1',
      [ROLE_CLAIM]: 'Member',
    });

    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');

    const req = httpMock.expectOne(`${API}/auth/login`);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);
    req.flush({ accessToken, expiresIn: 900, refreshToken: 'refresh-1' });

    await loginPromise;

    expect(localStorage.getItem('bookspace.accessToken')).toBe(accessToken);
    expect(localStorage.getItem('bookspace.refreshToken')).toBe('refresh-1');
    expect(service.claims()?.email).toBe('member1@acme.test');
    expect(service.isAuthenticated()).toBe(true);
  });

  it('clears the session on logout and does not toast a failed logout call', async () => {
    const accessToken = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    const logoutPromise = service.logout();
    const req = httpMock.expectOne(`${API}/auth/logout`);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);
    req.flush(null, { status: 500, statusText: 'Server Error' }); // fails server-side; must not throw

    await logoutPromise;

    expect(localStorage.getItem('bookspace.accessToken')).toBeNull();
    expect(localStorage.getItem('bookspace.refreshToken')).toBeNull();
    expect(service.claims()).toBeNull();
  });

  it('single-flights concurrent refresh calls into exactly one HTTP request', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    const first = firstValueFrom(service.refreshAccessToken());
    const second = firstValueFrom(service.refreshAccessToken());

    const accessToken2 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    // Exactly one — expectOne throws if either a second call was made, or none was.
    httpMock
      .expectOne(`${API}/auth/refresh`)
      .flush({ accessToken: accessToken2, expiresIn: 900, refreshToken: 'refresh-2' });

    expect(await first).toBe(accessToken2);
    expect(await second).toBe(accessToken2);
  });

  it('clears the session when a refresh attempt fails', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    const refreshPromise = firstValueFrom(service.refreshAccessToken());
    httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(refreshPromise).rejects.toBeTruthy();
    expect(service.claims()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });
});
