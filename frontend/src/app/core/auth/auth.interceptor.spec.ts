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
});
