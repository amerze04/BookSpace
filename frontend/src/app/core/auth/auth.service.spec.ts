import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { SKIP_ERROR_TOAST } from '../http/skip-error-toast';
import { buildFakeAccessToken } from './testing/jwt-fixture';

// A deterministic stand-in for the browser's own Web Locks queue: each
// request() call attaches its callback to a running promise chain, so
// callbacks execute strictly in call order and a later one only starts once
// the earlier one's returned promise has fully settled — exactly the
// "held for the callback's lifetime" guarantee navigator.locks itself makes.
function createFakeLockManager(): { request: (name: string, callback: () => Promise<unknown>) => Promise<unknown> } {
  let queue: Promise<unknown> = Promise.resolve();
  return {
    request: (_name: string, callback: () => Promise<unknown>) => {
      const run = queue.then(callback, callback);
      queue = run.catch(() => undefined);
      return run;
    },
  };
}

function stubWebLocks(manager: ReturnType<typeof createFakeLockManager>): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value: manager });
}

function restoreWebLocks(): void {
  Reflect.deleteProperty(navigator, 'locks');
}

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

  it('clears the session when a refresh attempt fails with a terminal 401', async () => {
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

  // Item 8: a refresh that fails because the network/server is unavailable
  // says nothing about whether the refresh token itself is still good, so it
  // must not be treated the same as the backend actually rejecting it.
  it('keeps the session when a refresh attempt fails with a network error, not a 401', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    const refreshPromise = firstValueFrom(service.refreshAccessToken());
    httpMock.expectOne(`${API}/auth/refresh`).error(new ProgressEvent('error'));

    await expect(refreshPromise).rejects.toBeTruthy();
    expect(localStorage.getItem('bookspace.accessToken')).toBe(accessToken1);
    expect(localStorage.getItem('bookspace.refreshToken')).toBe('refresh-1');
    expect(service.isAuthenticated()).toBe(true);
  });

  // Item 8, the 5xx/429 half of the same rule, checked separately since it's
  // a different HttpErrorResponse shape than a network-level error.
  it('keeps the session when a refresh attempt fails with a 503', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    const refreshPromise = firstValueFrom(service.refreshAccessToken());
    httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 503, statusText: 'Service Unavailable' });

    await expect(refreshPromise).rejects.toBeTruthy();
    expect(service.isAuthenticated()).toBe(true);
  });

  // Item 6: a refresh already in flight when logout() runs must not be able
  // to write its result afterward and resurrect the session logout just
  // ended — deterministic via the generation counter, no timing involved.
  it('does not let a refresh that was already in flight resurrect the session after logout', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    const refreshPromise = firstValueFrom(service.refreshAccessToken());

    const logoutPromise = service.logout();
    httpMock.expectOne(`${API}/auth/logout`).flush(null);
    await logoutPromise;

    expect(localStorage.getItem('bookspace.accessToken')).toBeNull();

    const accessToken2 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    httpMock
      .expectOne(`${API}/auth/refresh`)
      .flush({ accessToken: accessToken2, expiresIn: 900, refreshToken: 'refresh-2' });

    await expect(refreshPromise).rejects.toBeTruthy();
    expect(localStorage.getItem('bookspace.accessToken')).toBeNull();
    expect(localStorage.getItem('bookspace.refreshToken')).toBeNull();
    expect(service.claims()).toBeNull();
  });

  // Item 5: two tabs share localStorage but each has its own AuthService
  // instance. When another tab already holds the refresh lock, this tab must
  // wait for that tab's result — signalled via the `storage` event a real
  // second tab's localStorage write would fire — rather than making its own
  // POST /auth/refresh with the same refresh token (which decisions/0011
  // would see as reuse and revoke the whole family).
  it('waits for a peer tab already holding the refresh lock instead of calling refresh itself', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    // A peer tab claims the lock a moment before this tab tries to refresh.
    localStorage.setItem('bookspace.refreshLock', JSON.stringify({ id: 'peer-tab', acquiredAt: Date.now() }));

    const resultPromise = firstValueFrom(service.refreshAccessToken());

    httpMock.expectNone(`${API}/auth/refresh`);

    // The peer tab finishes its own refresh and writes the outcome directly
    // to the localStorage this tab shares with it, then the browser delivers
    // the `storage` event this tab's dispatchEvent call stands in for.
    const accessToken2 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    localStorage.setItem('bookspace.accessToken', accessToken2);
    localStorage.setItem('bookspace.refreshToken', 'refresh-2');
    localStorage.removeItem('bookspace.refreshLock');
    window.dispatchEvent(new StorageEvent('storage', { key: 'bookspace.accessToken', newValue: accessToken2 }));

    expect(await resultPromise).toBe(accessToken2);
    expect(service.isAuthenticated()).toBe(true);
  });

  // Item 5's other half: a peer tab that dies mid-refresh must not wedge this
  // tab out of ever refreshing. Fake timers make the wait deterministic
  // rather than a real 8-second sleep.
  it('takes over and refreshes itself if the peer holding the lock never produces a result', async () => {
    vi.useFakeTimers();
    try {
      const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
      httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
      await loginPromise;

      localStorage.setItem('bookspace.refreshLock', JSON.stringify({ id: 'peer-tab', acquiredAt: Date.now() }));

      const resultPromise = firstValueFrom(service.refreshAccessToken());
      httpMock.expectNone(`${API}/auth/refresh`);

      await vi.advanceTimersByTimeAsync(9_000);

      const accessToken2 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      httpMock
        .expectOne(`${API}/auth/refresh`)
        .flush({ accessToken: accessToken2, expiresIn: 900, refreshToken: 'refresh-2' });

      expect(await resultPromise).toBe(accessToken2);
    } finally {
      vi.useRealTimers();
    }
  });

  // Item 2: navigator.locks is a true mutex, so it's used ahead of the
  // best-effort localStorage lock whenever it's available — see
  // refresh-lock.ts. These use a deterministic fake lock manager (a strictly
  // ordered promise queue) instead of the real browser API, so the
  // interleaving is exact rather than timing-dependent.
  describe('cross-tab refresh coordination via Web Locks', () => {
    afterEach(() => {
      restoreWebLocks();
    });

    it('serializes two tabs through the Web Lock into exactly one network refresh', async () => {
      stubWebLocks(createFakeLockManager());

      const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
      httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
      await loginPromise;

      // A second AuthService instance stands in for a second tab, sharing
      // this same localStorage and the same fake lock manager.
      const tabB = new AuthService(TestBed.inject(HttpClient));

      const first = firstValueFrom(service.refreshAccessToken());
      const second = firstValueFrom(tabB.refreshAccessToken());
      // Let the lock-queued callbacks actually run before asserting on the
      // HTTP traffic they produce.
      await Promise.resolve();
      await Promise.resolve();

      const accessToken2 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      // Exactly one — expectOne throws if tabB also made its own call.
      httpMock
        .expectOne(`${API}/auth/refresh`)
        .flush({ accessToken: accessToken2, expiresIn: 900, refreshToken: 'refresh-2' });

      expect(await first).toBe(accessToken2);
      expect(await second).toBe(accessToken2);
    });

    it('prefers the Web Lock over the localStorage lock when both are available', async () => {
      stubWebLocks(createFakeLockManager());

      const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
      httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
      await loginPromise;

      // If the localStorage lock were still consulted first, this would make
      // refreshAccessToken() believe a peer holds it and wait indefinitely
      // instead of refreshing through the Web Lock.
      localStorage.setItem('bookspace.refreshLock', JSON.stringify({ id: 'irrelevant', acquiredAt: Date.now() }));

      const resultPromise = firstValueFrom(service.refreshAccessToken());
      // Let the lock-queued callback actually run before asserting on the
      // HTTP traffic it produces.
      await Promise.resolve();
      await Promise.resolve();

      const accessToken2 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
      httpMock
        .expectOne(`${API}/auth/refresh`)
        .flush({ accessToken: accessToken2, expiresIn: 900, refreshToken: 'refresh-2' });

      expect(await resultPromise).toBe(accessToken2);
    });
  });

  // Cross-tab sync outside a refresh entirely: a logout in another tab must
  // end this tab's session too, since they share the same localStorage.
  it('clears its own session when another tab logs out', async () => {
    const accessToken1 = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    const loginPromise = service.login('member1@acme.test', 'Passw0rd!');
    httpMock.expectOne(`${API}/auth/login`).flush({ accessToken: accessToken1, expiresIn: 900, refreshToken: 'refresh-1' });
    await loginPromise;

    localStorage.removeItem('bookspace.accessToken');
    localStorage.removeItem('bookspace.refreshToken');
    window.dispatchEvent(new StorageEvent('storage', { key: 'bookspace.accessToken', newValue: null }));

    expect(service.claims()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });
});
