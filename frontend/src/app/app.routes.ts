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
    canActivate: [authGuard],
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
            // WP-7 Phase 2 replaces this with the real availability screen.
            path: ':id/availability',
            data: { title: 'Availability' },
            loadComponent: () =>
              import('./features/placeholder/placeholder.component').then((m) => m.PlaceholderComponent),
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
