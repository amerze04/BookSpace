import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

// Blocks a protected route for anyone without a session, remembering where
// they were headed so login can send them straight there afterwards.
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

// The mirror image: keeps an already-signed-in visitor off the login page.
export const guestOnlyGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAuthenticated() ? router.createUrlTree(['/']) : true;
};

// Typing the URL directly is still a route, guard or no nav link pointing at
// it — this is what stops a Member reaching /approvals that way. UI-only
// enforcement: the backend independently refuses the actual approve/reject
// calls to anyone but an eligible approver (decision 0018), guard or not.
export const approverGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.canApproveBookings() ? true : router.createUrlTree(['/home']);
};
