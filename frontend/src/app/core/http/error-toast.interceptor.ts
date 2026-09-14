import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { NotificationService } from '../notifications/notification.service';
import { isProblemDetails } from './problem-details';
import { SKIP_ERROR_TOAST } from './skip-error-toast';

// The catch-all: anything that reaches here had no more specific handler
// already showing the user something. Two things are deliberately excluded,
// not oversights:
//  - a 401 is always auth.interceptor's domain — it either retries
//    successfully (nothing to report) or fails and is already redirecting
//    with its own specific message; a second, generic toast on top would
//    just repeat or contradict that one.
//  - anything the request itself opted out of via SKIP_ERROR_TOAST, because
//    that caller has its own inline feedback for its own failures.
export const errorToastInterceptor: HttpInterceptorFn = (request, next) => {
  const notifications = inject(NotificationService);

  return next(request).pipe(
    catchError((error: unknown) => {
      const isUnauthorized = error instanceof HttpErrorResponse && error.status === 401;
      const isOptedOut = request.context.get(SKIP_ERROR_TOAST);

      if (!isUnauthorized && !isOptedOut) {
        notifications.show(describeError(error));
      }

      return throwError(() => error);
    }),
  );
};

function describeError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'Could not reach the server. Check your connection and try again.';
    }
    if (isProblemDetails(error.error)) {
      return error.error.title;
    }
  }
  return 'Something went wrong. Please try again.';
}
