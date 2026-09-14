import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { authInterceptor } from './core/auth/auth.interceptor';
import { errorToastInterceptor } from './core/http/error-toast.interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // Order matters, same as ASP.NET Core middleware: the first entry wraps
    // everything after it. errorToastInterceptor has to be outermost so it
    // only ever sees whatever authInterceptor couldn't already fix (a
    // successful silent refresh means no error reaches this far at all) —
    // reversed, the toast would fire on every expired-token 401 before
    // authInterceptor ever got a chance to quietly refresh and retry it.
    provideHttpClient(withInterceptors([errorToastInterceptor, authInterceptor])),
  ]
};
