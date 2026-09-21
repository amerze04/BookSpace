import { Routes } from '@angular/router';
import { approverGuard, authGuard, guestOnlyGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestOnlyGuard],
    loadComponent: () => import('./features/auth/components/login/login.component').then((m) => m.LoginComponent),
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
    loadComponent: () => import('./layout/components/shell/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        // WP-7 Phase 4 step 2. The calendar is the landing screen: Home had
        // been a placeholder since WP-6 with no job assigned to it in the PRD
        // or any work package, and "what am I booked for" is what a member
        // opens this app to answer.
        path: 'calendar',
        data: { title: 'Calendar' },
        loadComponent: () =>
          import('./features/calendar/components/calendar/calendar.component').then((m) => m.CalendarComponent),
      },
      // Kept as a redirect rather than deleted outright: /home was the app's
      // landing route for two work packages, so bookmarks and any link written
      // before 2026-09-18 still resolve instead of falling through to the
      // catch-all and bouncing the visitor to /login.
      { path: 'home', pathMatch: 'full', redirectTo: 'calendar' },
      {
        path: 'resources',
        children: [
          {
            path: '',
            data: { title: 'Resources' },
            loadComponent: () =>
              import('./features/resources/components/resource-list/resource-list.component').then((m) => m.ResourceListComponent),
          },
          {
            path: ':id',
            data: { title: 'Resource details' },
            loadComponent: () =>
              import('./features/resources/components/resource-detail/resource-detail.component').then(
                (m) => m.ResourceDetailComponent,
              ),
          },
          {
            path: ':id/availability',
            data: { title: 'Availability' },
            loadComponent: () =>
              import('./features/availability/components/availability/availability.component').then((m) => m.AvailabilityComponent),
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
              import('./features/booking/components/booking/booking.component').then((m) => m.BookingComponent),
          },
        ],
      },
      {
        // WP-7 Phase 4 step 4. A top-level route rather than a child of the
        // calendar: FR-5.2 makes each occurrence independently viewable, and a
        // booking reached by a bookmark or a pasted link must resolve without
        // the calendar having been visited first.
        path: 'bookings/:id',
        data: { title: 'Booking' },
        loadComponent: () =>
          import('./features/booking/components/booking-detail/booking-detail.component').then(
            (m) => m.BookingDetailComponent,
          ),
      },
      // `my-bookings` was removed in WP-7 Phase 4 (2026-09-18), not repointed:
      // a separate list is redundant once the calendar shows the same bookings,
      // and the source work package never asked for the screen. The three links
      // that pointed at it now point at /calendar.
      {
        path: 'approvals',
        data: { title: 'Approvals' },
        canActivate: [approverGuard],
        loadComponent: () =>
          import('./features/placeholder/components/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      {
        path: 'settings',
        data: { title: 'Settings' },
        loadComponent: () =>
          import('./features/placeholder/components/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      {
        path: 'help',
        data: { title: 'Help' },
        loadComponent: () =>
          import('./features/placeholder/components/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
      },
      { path: '', pathMatch: 'full', redirectTo: 'calendar' },
    ],
  },
  { path: '**', redirectTo: 'login' },
];
