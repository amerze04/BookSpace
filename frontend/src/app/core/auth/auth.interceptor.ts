import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

// Never try to silent-refresh a 401 from these — that 401 already IS the
// answer (wrong credentials, or the refresh token itself was rejected), and
// retrying it as if the access token just expired would loop.
const AUTH_ENDPOINTS = ['/auth/login', '/auth/refresh'];

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

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
          router.navigateByUrl('/login');
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};

function withBearerToken(request: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
}
