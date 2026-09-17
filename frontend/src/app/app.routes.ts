import { Routes } from '@angular/router';
import { approverGuard, authGuard, guestOnlyGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestOnlyGuard],
    loadComponent: () => import('./features/auth/login/login.component').then((m) => m.LoginComponent),
  },
  {
    path: '',
    // Both are needed: canActivate guards entry into the shell itself, but
    // once the shell is active, navigating between its already-loaded
    // children (home -> resources -> my-bookings, ...) never re-runs it —
    // canActivateChild is what re-validates the session (and transparently
    // refreshes an expired access token via AuthService.hasValidSession(),
    // see auth.guard.ts) on every one of those child navigations too.
    canActivate: [authGuard],
    canActivateChild: [authGuard],
    loadComponent: () => import('./layout/shell/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        path: 'home',
        data: { title: 'Home' },
        loadComponent: () =>
          import('./features/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      {
        path: 'resources',
        children: [
          {
            path: '',
            data: { title: 'Resources' },
            loadComponent: () =>
              import('./features/resources/list/resource-list.component').then((m) => m.ResourceListComponent),
          },
          {
            path: ':id',
            data: { title: 'Resource details' },
            loadComponent: () =>
              import('./features/resources/detail/resource-detail.component').then(
                (m) => m.ResourceDetailComponent,
              ),
          },
          {
            path: ':id/availability',
            data: { title: 'Availability' },
            loadComponent: () =>
              import('./features/availability/availability.component').then((m) => m.AvailabilityComponent),
          },
          {
            // WP-7 Phase 3 step 2: the placeholder this route carried since
            // Phase 1 is now the real booking screen. The availability
            // screen's "Continue to booking" navigates here carrying the
            // selected span via router state.
            //
            // 'Book resource' rather than the design's shorter 'Book' crumb:
            // ShellComponent derives the page heading *from* the last crumb
            // deliberately (one source for both), so the two cannot differ
            // without reopening that decision for one screen. The design's
            // heading is the more prominent of the two, so it wins; the crumb
            // reads 'Resources > Conference Room A > Book resource'.
            path: ':id/book',
            data: { title: 'Book resource' },
            loadComponent: () =>
              import('./features/booking/booking.component').then((m) => m.BookingComponent),
          },
        ],
      },
      {
        path: 'my-bookings',
        data: { title: 'My Bookings' },
        loadComponent: () =>
          import('./features/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      {
        path: 'approvals',
        data: { title: 'Approvals' },
        canActivate: [approverGuard],
        loadComponent: () =>
          import('./features/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      {
        path: 'settings',
        data: { title: 'Settings' },
        loadComponent: () =>
          import('./features/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      {
        path: 'help',
        data: { title: 'Help' },
        loadComponent: () =>
          import('./features/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      { path: '', pathMatch: 'full', redirectTo: 'home' },
    ],
  },
  { path: '**', redirectTo: 'login' },
];
