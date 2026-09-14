import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { errorToastInterceptor } from './error-toast.interceptor';
import { NotificationService } from '../notifications/notification.service';
import { skipErrorToast } from './skip-error-toast';

const API = 'http://localhost:5270';

describe('errorToastInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorToastInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => httpMock.verify());

  it("shows the backend's ProblemDetails title for a mapped failure", () => {
    http.get(`${API}/resources/1`).subscribe({ error: () => {} });

    httpMock
      .expectOne(`${API}/resources/1`)
      .flush(
        { title: 'The requested resource was not found.', status: 404, reasonCode: 'ResourceNotFound', correlationId: 'c1' },
        { status: 404, statusText: 'Not Found' },
      );

    expect(notifications.notifications().map((n) => n.message)).toContain('The requested resource was not found.');
  });

  it('falls back to a generic message when the server cannot be reached at all', () => {
    http.get(`${API}/resources`).subscribe({ error: () => {} });

    httpMock.expectOne(`${API}/resources`).error(new ProgressEvent('error'), { status: 0 });

    expect(notifications.notifications().map((n) => n.message)).toContain(
      'Could not reach the server. Check your connection and try again.',
    );
  });

  it("never toasts a 401 — that status is always auth.interceptor's job", () => {
    http.get(`${API}/resources`).subscribe({ error: () => {} });
    httpMock.expectOne(`${API}/resources`).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(notifications.notifications()).toEqual([]);
  });

  it('stays silent for a request that opted out via skipErrorToast()', () => {
    http.post(`${API}/auth/login`, {}, { context: skipErrorToast() }).subscribe({ error: () => {} });

    httpMock
      .expectOne(`${API}/auth/login`)
      .flush(
        { title: 'One or more validation errors occurred.', status: 400, reasonCode: 'ValidationFailed', correlationId: 'c2', errors: {} },
        { status: 400, statusText: 'Bad Request' },
      );

    expect(notifications.notifications()).toEqual([]);
  });
});
