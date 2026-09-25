import { Routes } from '@angular/router';
import { adminGuard, approverGuard, authGuard, guestOnlyGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestOnlyGuard],
    loadComponent: () => import('./features/auth/components/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // Where an emailed invitation lands (user management, the activation phase
    // — inserted between 6 and 7 after the owner found the link resolving to
    // /login; see the component for how the plan came to miss it).
    //
    // A sibling of `login`, outside the shell: the recipient has no account
    // yet, so `authGuard` would bounce them to the very screen they cannot use.
    //
    // **Deliberately no `guestOnlyGuard`**, unlike `login`. Somebody already
    // signed in on a shared machine must still be able to redeem their own
    // link, and the backend supports exactly that — the auth interceptor
    // attaches their bearer token and `POST /auth/activate` handles the
    // mismatched tenant rather than failing (user management phase 2 built and
    // tested that case). Bouncing them to the calendar would be this client
    // refusing something the server allows.
    path: 'activate',
    loadComponent: () =>
      import('./features/auth/components/activate/activate.component').then((m) => m.ActivateComponent),
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
        // WP-7 Phase 6 step 3: the placeholder this route has carried since
        // WP-6 is now the real approval queue — the last unbuilt screen in the
        // package.
        //
        // approverGuard stays, and it is doing two different jobs. It keeps a
        // plain member out of a screen whose only request they would be 400'd
        // for making (`scope=tenant` is refused for them), and it means the
        // component never has to render a "you have no reach here" state —
        // which would otherwise be indistinguishable from an empty queue.
        path: 'approvals',
        data: { title: 'Approvals' },
        canActivate: [approverGuard],
        loadComponent: () =>
          import('./features/approvals/components/approval-queue/approval-queue.component').then(
            (m) => m.ApprovalQueueComponent,
          ),
      },
      {
        // Admin console phase 2 (docs/admin-plan.md). The tenant administration
        // screens live on their own route tree rather than as an "admin mode"
        // on /resources, which was the phase-2 call the plan left open (§7).
        //
        // Three reasons, in order of weight. The two lists answer different
        // questions — /resources is "find something to book" and hides archived
        // rows by design (FR-3.5), while this one is "manage the catalogue" and
        // has to show them. WP-7's booking detail screen is the cautionary tale
        // for the alternative: one screen serving two audiences needed
        // `viewerIsOwner` threaded through every string, and shipped telling an
        // approver "the time is not held for *you* yet" about someone else's
        // request. And a separate tree means adminGuard protects the whole
        // console once, instead of every button re-deciding who may see it.
        //
        // The guard is on the parent, so it covers every child added in phases
        // 3-6 without each remembering to ask for it.
        path: 'admin',
        data: { title: 'Admin' },
        canActivate: [adminGuard],
        children: [
          // **Flat siblings, not a nested `resources` group**, and that is a
          // correctness constraint rather than a style choice. Angular's
          // default `paramsInheritanceStrategy` ('emptyOnly') copies a parent's
          // `data` onto any child that has an empty path *or no component* — so
          // a componentless `resources` grouping route would inherit this
          // parent's `title: 'Admin'` and the breadcrumb walk would read
          // "Admin > Admin > Resources". The member-facing `resources` group
          // gets away with the same shape only because its parent carries no
          // title to inherit. Every route below loads a component, so none of
          // them inherits anything.
          {
            // Phase 3: the placeholder this route carried since phase 2 is now
            // the real admin resource list.
            path: 'resources',
            data: { title: 'Resources' },
            loadComponent: () =>
              import('./features/admin/components/admin-resource-list/admin-resource-list.component').then(
                (m) => m.AdminResourceListComponent,
              ),
          },
          {
            // **Before `resources/:id`, and the order is load-bearing**: the
            // router matches in declaration order, so the parameterised route
            // declared first would swallow `/admin/resources/new` and try to
            // load a resource whose id is the string "new".
            path: 'resources/new',
            data: { title: 'New resource' },
            loadComponent: () =>
              import('./features/admin/components/admin-resource-form/admin-resource-form.component').then(
                (m) => m.AdminResourceFormComponent,
              ),
          },
          {
            // The same component as `resources/new`. Its mode comes from
            // whether this `:id` is present — see the component for why one
            // form serves both.
            //
            // 'Resource' is only the fallback crumb: the form replaces it with
            // the resource's own name through BreadcrumbService once it has
            // loaded, the way the member-facing detail screen does.
            path: 'resources/:id',
            data: { title: 'Resource' },
            loadComponent: () =>
              import('./features/admin/components/admin-resource-form/admin-resource-form.component').then(
                (m) => m.AdminResourceFormComponent,
              ),
          },
          {
            // Phase 4. A sibling of `resources/:id` rather than a child, mirroring
            // how the member-facing availability screen sits beside the resource
            // detail — so the crumb chain ends in "Availability" and the resource's
            // own name is *inserted* before it rather than replacing it.
            path: 'resources/:id/availability-windows',
            data: { title: 'Availability' },
            loadComponent: () =>
              import(
                './features/admin/components/admin-availability-windows/admin-availability-windows.component'
              ).then((m) => m.AdminAvailabilityWindowsComponent),
          },
          {
            // Phase 5. A sibling of `resources/:id` for the same reason the
            // schedule is — the crumb chain ends in "Approvers" and the
            // resource's name is inserted before it.
            path: 'resources/:id/approvers',
            data: { title: 'Approvers' },
            loadComponent: () =>
              import('./features/admin/components/admin-approvers/admin-approvers.component').then(
                (m) => m.AdminApproversComponent,
              ),
          },
          {
            // Phase 6, the last screen. Same sibling shape as the two above.
            path: 'resources/:id/blackout-periods',
            data: { title: 'Blackouts' },
            loadComponent: () =>
              import('./features/admin/components/admin-blackouts/admin-blackouts.component').then(
                (m) => m.AdminBlackoutsComponent,
              ),
          },
          {
            // User management phase 6. A sibling of `resources`, not a child of
            // it: `GET /users` and `POST /users` are top-level routes, unlike
            // the windows/approvers/blackout screens above, which are all
            // sub-resources of a resource and can only be reached with one in
            // hand.
            path: 'users',
            data: { title: 'Users' },
            loadComponent: () =>
              import('./features/admin/components/admin-user-list/admin-user-list.component').then(
                (m) => m.AdminUserListComponent,
              ),
          },
          {
            // **Before any `users/:id`**, whenever phase 7 adds one — the
            // router matches in declaration order, and a parameterised route
            // declared first would swallow `/admin/users/new` and try to load a
            // user whose id is the string "new". The same trap
            // `resources/new` documents.
            path: 'users/new',
            data: { title: 'Invite someone' },
            loadComponent: () =>
              import('./features/admin/components/admin-user-form/admin-user-form.component').then(
                (m) => m.AdminUserFormComponent,
              ),
          },
          { path: '', pathMatch: 'full', redirectTo: 'resources' },
        ],
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
