import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { NotificationService } from '../notifications/notification.service';
import { AuthService } from './auth.service';

// Never try to silent-refresh a 401 from these — that 401 already IS the
// answer (wrong credentials, or the refresh token itself was rejected), and
// retrying it as if the access token just expired would loop.
const AUTH_ENDPOINTS = ['/auth/login', '/auth/refresh'];

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  // Only ever attach BookSpace's own access token, and only ever run its
  // refresh dance, against BookSpace's own API. Every request this app makes
  // to its backend is built from environment.apiBaseUrl (no relative URLs
  // anywhere in the app), so this is a precise test, not a heuristic — and it
  // is load-bearing: this interceptor is registered for every HttpClient
  // request in the app, so without this check a bearer token minted for this
  // backend would be sent to whatever other host a future feature calls
  // (a map tile provider, a file upload target, anything), and that third
  // party's own 401 would trigger this app's refresh-and-retry logic against
  // its URL.
  if (!request.url.startsWith(environment.apiBaseUrl)) {
    return next(request);
  }

  const auth = inject(AuthService);
  const router = inject(Router);
  const notifications = inject(NotificationService);

  const authorizedRequest = withBearerToken(request, auth.accessToken);

  return next(authorizedRequest).pipe(
    catchError((error: unknown) => {
      const isUnauthorized = error instanceof HttpErrorResponse && error.status === 401;
      const isAuthEndpoint = AUTH_ENDPOINTS.some((path) => request.url.includes(path));

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
