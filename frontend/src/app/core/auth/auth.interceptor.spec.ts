import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { authInterceptor } from './auth.interceptor';
import { NotificationService } from '../notifications/notification.service';
import { buildFakeAccessToken } from './testing/jwt-fixture';

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const API = 'http://localhost:5270';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let notifications: NotificationService;
  let router: Router;

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('bookspace.accessToken', 'expired-token');
    localStorage.setItem('bookspace.refreshToken', 'refresh-1');

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('attaches the stored access token as a bearer header', () => {
    http.get(`${API}/resources`).subscribe();

    const req = httpMock.expectOne(`${API}/resources`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer expired-token');
    req.flush({ items: [] });
  });

  it('never attaches the BookSpace token to a request outside apiBaseUrl, and never refreshes on its 401', () => {
    let error: unknown;
    http.get('https://maps.example.com/tiles/1/2/3').subscribe({ error: (e) => (error = e) });

    const req = httpMock.expectOne('https://maps.example.com/tiles/1/2/3');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush(null, { status: 401, statusText: 'Unauthorized' });

    httpMock.expectNone(`${API}/auth/refresh`);
    expect(error).toBeTruthy();
  });

  // Adversarial: a same-origin scoping test built on startsWith/includes can
  // be fooled by a lookalike URL that merely shares a text prefix with
  // apiBaseUrl. These all pass `'...'.startsWith(API)` yet are NOT the
  // BookSpace API, so the token must never reach them and their 401s must
  // never trigger a refresh.
  describe('rejects lookalike origins that merely share a text prefix with apiBaseUrl', () => {
    const lookalikes = [
      // Extra digit on the port — a raw prefix match still succeeds.
      'http://localhost:52700/resources',
      // apiBaseUrl as a subdomain-ish text prefix of a different host.
      'http://localhost:5270.evil.com/resources',
      // Different host entirely; same port.
      'http://evil.localhost:5270/resources',
      // Same host/port, different scheme — a real different origin.
      'https://localhost:5270/resources',
    ];

    for (const url of lookalikes) {
      it(`never attaches the token to ${url}, and never refreshes on its 401`, () => {
        let error: unknown;
        http.get(url).subscribe({ error: (e) => (error = e) });

        const req = httpMock.expectOne(url);
        expect(req.request.headers.has('Authorization')).toBe(false);
        req.flush(null, { status: 401, statusText: 'Unauthorized' });

        httpMock.expectNone(`${API}/auth/refresh`);
        expect(error).toBeTruthy();
      });
    }
  });

  it('treats a path that merely starts with an auth endpoint\'s text as a normal request, refreshing on its 401', () => {
    // "/auth/refresh-status" contains "/auth/refresh" as a substring, which a
    // request.url.includes('/auth/refresh') check would wrongly treat as the
    // refresh endpoint itself and skip retrying — it's a different endpoint
    // and its 401 should go through the normal silent-refresh-and-retry path.
    let result: unknown;
    http.get(`${API}/auth/refresh-status`).subscribe((response) => (result = response));

    httpMock.expectOne(`${API}/auth/refresh-status`).flush(null, { status: 401, statusText: 'Unauthorized' });

    const newAccessToken = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    httpMock
      .expectOne(`${API}/auth/refresh`)
      .flush({ accessToken: newAccessToken, expiresIn: 900, refreshToken: 'refresh-2' });

    const retried = httpMock.expectOne(`${API}/auth/refresh-status`);
    expect(retried.request.headers.get('Authorization')).toBe(`Bearer ${newAccessToken}`);
    retried.flush({ ok: true });

    expect(result).toEqual({ ok: true });
  });

  it('on a 401, silently refreshes and retries the original request with the new token', () => {
    let result: unknown;
    http.get(`${API}/resources`).subscribe((response) => (result = response));

    httpMock.expectOne(`${API}/resources`).flush(null, { status: 401, statusText: 'Unauthorized' });

    const newAccessToken = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    httpMock
      .expectOne(`${API}/auth/refresh`)
      .flush({ accessToken: newAccessToken, expiresIn: 900, refreshToken: 'refresh-2' });

    const retried = httpMock.expectOne(`${API}/resources`);
    expect(retried.request.headers.get('Authorization')).toBe(`Bearer ${newAccessToken}`);
    retried.flush({ items: [] });

    expect(result).toEqual({ items: [] });
  });

  it('single-flights a refresh across two requests that fail at the same moment', () => {
    http.get(`${API}/resources`).subscribe();
    http.get(`${API}/my-bookings`).subscribe();

    httpMock.expectOne(`${API}/resources`).flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne(`${API}/my-bookings`).flush(null, { status: 401, statusText: 'Unauthorized' });

    const newAccessToken = buildFakeAccessToken({ sub: 'u1', email: 'member1@acme.test', orgId: 'org-1', [ROLE_CLAIM]: 'Member' });
    // Exactly one refresh call — expectOne throws if a second one also went out.
    httpMock
      .expectOne(`${API}/auth/refresh`)
      .flush({ accessToken: newAccessToken, expiresIn: 900, refreshToken: 'refresh-2' });

    httpMock.expectOne(`${API}/resources`).flush({ ok: true });
    httpMock.expectOne(`${API}/my-bookings`).flush({ ok: true });
  });

  it('never tries to refresh a 401 that came from the auth endpoints themselves', () => {
    let error: unknown;
    http.post(`${API}/auth/login`, {}).subscribe({ error: (e) => (error = e) });

    httpMock.expectOne(`${API}/auth/login`).flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectNone(`${API}/auth/refresh`);

    expect(error).toBeTruthy();
  });

  it('sends the user to /login and shows a notification when the refresh itself fails', () => {
    const navigateSpy = vi.spyOn(router, 'navigateByUrl');

    http.get(`${API}/resources`).subscribe({ error: () => {} });
    httpMock.expectOne(`${API}/resources`).flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(navigateSpy).toHaveBeenCalledWith('/login');
    expect(notifications.notifications().some((n) => n.message.includes('session has expired'))).toBe(true);
  });

  // Bug fix (item 8's other half, found while verifying the hardening pass):
  // AuthService.refreshAccessToken() only clears the session for a terminal
  // (401) failure — a transient one (offline, a 5xx, a 429) leaves the
  // tokens exactly as they were. This interceptor used to navigate to
  // /login and show "session has expired" unconditionally on *any* refresh
  // failure, contradicting that distinction the moment a user could
  // actually see it: a real session, still valid in storage, forcibly
  // logged out by a network blip.
  it('does not navigate to /login or claim the session expired when the refresh itself fails transiently', () => {
    // A real (if expired) JWT, unlike beforeEach's placeholder string —
    // needed here specifically because the interceptor's fix checks
    // auth.isAuthenticated(), which reads the decoded claims: a genuinely
    // undecodable stored token would already read as "not authenticated"
    // before the refresh ever ran, which would pass this assertion for the
    // wrong reason.
    const expiredAccessToken = buildFakeAccessToken({
      sub: 'u1',
      email: 'member1@acme.test',
      orgId: 'org-1',
      [ROLE_CLAIM]: 'Member',
      exp: Math.floor(Date.now() / 1000) - 60,
    });
    localStorage.setItem('bookspace.accessToken', expiredAccessToken);

    const navigateSpy = vi.spyOn(router, 'navigateByUrl');

    http.get(`${API}/resources`).subscribe({ error: () => {} });
    httpMock.expectOne(`${API}/resources`).flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne(`${API}/auth/refresh`).flush(null, { status: 0, statusText: 'Unknown Error' });

    expect(navigateSpy).not.toHaveBeenCalledWith('/login');
    expect(notifications.notifications().some((n) => n.message.includes('session has expired'))).toBe(false);
    // The session itself is untouched — a network blip is not a logout.
    expect(localStorage.getItem('bookspace.accessToken')).toBe(expiredAccessToken);
    expect(localStorage.getItem('bookspace.refreshToken')).toBe('refresh-1');
  });
});
