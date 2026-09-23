import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './auth.service';

// Blocks a protected route for anyone without a session, remembering where
// they were headed so login can send them straight there afterwards.
//
// Async because "does this visitor have a session" is no longer just "is
// there a decodable token" (item 7): a live, non-expired access token passes
// straight through, but one that has expired needs a refresh attempt first —
// AuthService.hasValidSession() is the one place that decision is made, so
// this guard and the HTTP interceptor can never disagree about it.
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth
    .hasValidSession()
    .pipe(map((valid) => valid || router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } })));
};

// The mirror image: keeps an already-signed-in visitor off the login page.
export const guestOnlyGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.hasValidSession().pipe(map((valid) => (valid ? router.createUrlTree(['/']) : true)));
};

// Typing the URL directly is still a route, guard or no nav link pointing at
// it — this is what stops a Member reaching /approvals that way. UI-only
// enforcement: the backend independently refuses the actual approve/reject
// calls to anyone but an eligible approver (decision 0018), guard or not.
export const approverGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  // /calendar since WP-7 Phase 4 (was /home, which is now itself a redirect
  // here) — bounced to the app's landing screen rather than through a redirect
  // hop, so the resulting URL is the one the visitor actually ends up on.
  return auth.canApproveBookings() ? true : router.createUrlTree(['/calendar']);
};

// Admin console phase 2. The same job approverGuard does, for /admin: typing
// the URL directly is still a route, and this is what stops a Member or an
// Approver reaching the tenant administration screens that way.
//
// UI-only enforcement, exactly as above — every admin endpoint independently
// refuses anyone but a TenantAdmin (WP-3's acceptance criterion, "non-admins
// cannot create or edit resources"), guard or not. What this actually buys is
// that nobody is shown a console whose every request would come back 403.
//
// It deliberately does **not** admit a SysAdmin, even though the backend's
// TenantAdmin policy does by role — see AuthService.isTenantAdmin for why.
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isTenantAdmin() ? true : router.createUrlTree(['/calendar']);
};
