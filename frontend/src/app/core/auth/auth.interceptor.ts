import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { NotificationService } from '../notifications/notification.service';
import { AuthService } from './auth.service';

// The configured API origin, parsed once. `pathname` is normalized to strip
// a trailing slash so a configured base path (if one is ever added, e.g. a
// reverse-proxied "/api") has a clean boundary to test against below.
const API_BASE_URL = new URL(environment.apiBaseUrl);
const API_BASE_PATH = API_BASE_URL.pathname.replace(/\/+$/, '');

// Never try to silent-refresh a 401 from these — that 401 already IS the
// answer (wrong credentials, or the refresh token itself was rejected), and
// retrying it as if the access token just expired would loop. Resolved
// against API_BASE_PATH so this still lines up if a base path is ever added.
const AUTH_ENDPOINT_PATHS = new Set(['/auth/login', '/auth/refresh'].map((path) => `${API_BASE_PATH}${path}`));

// Deliberately NOT a startsWith/includes substring test — those are fooled by
// a lookalike origin that merely shares a text prefix, e.g.
// "http://localhost:52700" or "http://localhost:5270.evil.com" both pass
// `startsWith('http://localhost:5270')`. Parsing both sides with URL and
// comparing the resolved `origin` (scheme + host + port) is exact. Requests
// with an unparseable URL are treated as external — safe, since apiBaseUrl
// requests are always well-formed absolute URLs in this app.
function isBookSpaceApiRequest(requestUrl: string): boolean {
  let url: URL;
  try {
    url = new URL(requestUrl, API_BASE_URL);
  } catch {
    return false;
  }

  if (url.origin !== API_BASE_URL.origin) {
    return false;
  }

  // Only matters once a base path is configured — today API_BASE_PATH is ''
  // (root), so every same-origin request qualifies. `startsWith` here is
  // safe because it's anchored to a "/" boundary segment, not a raw prefix
  // of the full URL — "/api2/..." cannot match a "/api" base path.
  if (!API_BASE_PATH) {
    return true;
  }
  return url.pathname === API_BASE_PATH || url.pathname.startsWith(`${API_BASE_PATH}/`);
}

function isAuthEndpointRequest(requestUrl: string): boolean {
  try {
    return AUTH_ENDPOINT_PATHS.has(new URL(requestUrl, API_BASE_URL).pathname);
  } catch {
    return false;
  }
}

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  // Only ever attach BookSpace's own access token, and only ever run its
  // refresh dance, against BookSpace's own API. This interceptor is
  // registered for every HttpClient request in the app, so without this
  // check a bearer token minted for this backend would be sent to whatever
  // other host a future feature calls (a map tile provider, a file upload
  // target, anything), and that third party's own 401 would trigger this
  // app's refresh-and-retry logic against its URL.
  if (!isBookSpaceApiRequest(request.url)) {
    return next(request);
  }

  const auth = inject(AuthService);
  const router = inject(Router);
  const notifications = inject(NotificationService);

  const authorizedRequest = withBearerToken(request, auth.accessToken);

  return next(authorizedRequest).pipe(
    catchError((error: unknown) => {
      const isUnauthorized = error instanceof HttpErrorResponse && error.status === 401;
      const isAuthEndpoint = isAuthEndpointRequest(request.url);

      if (!isUnauthorized || isAuthEndpoint) {
        return throwError(() => error);
      }

      return auth.refreshAccessToken().pipe(
        switchMap((newAccessToken) => next(withBearerToken(request, newAccessToken))),
        catchError((refreshError) => {
          // Bug fix (item 8's other half): AuthService.refreshAccessToken()
          // only clears the session for a *terminal* failure (a real 401 —
          // expired, reused, or the account/org gone inactive); a transient
          // one (offline, a 5xx, a 429) leaves the tokens in storage exactly
          // as they were. Telling the user their session expired, and
          // forcibly navigating them to /login, when it did not, contradicts
          // that distinction at the one place a person actually sees it —
          // auth.isAuthenticated() is what the terminal path already made
          // false, so checking it here is what keeps this interceptor from
          // disagreeing with the service it is calling.
          if (!auth.isAuthenticated()) {
            notifications.show('Your session has expired. Please sign in again.', 'info');
            router.navigateByUrl('/login');
          }
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};

function withBearerToken(request: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
}
