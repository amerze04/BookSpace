import { HttpContext, HttpContextToken } from '@angular/common/http';

// Request-local metadata that never leaves the browser — HttpContext travels
// alongside a request through the interceptor chain but is never serialized
// onto the wire. This is how a caller opts a specific call out of the global
// toast: it already has its own inline way of showing the same failure
// (LoginComponent's error message), so a second, generic toast on top would
// just be noise for the one failure the app actually explains well.
export const SKIP_ERROR_TOAST = new HttpContextToken<boolean>(() => false);

export function skipErrorToast(): HttpContext {
  return new HttpContext().set(SKIP_ERROR_TOAST, true);
}
