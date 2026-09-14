import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { BrandMarkComponent } from '../../shared/brand-mark/brand-mark.component';

interface NavItem {
  label: string;
  path: string;
  icon: 'home' | 'resources' | 'bookings' | 'approvals' | 'settings' | 'help';
}

const BASE_PRIMARY_NAV_ITEMS: NavItem[] = [
  { label: 'Home', path: '/home', icon: 'home' },
  { label: 'Resources', path: '/resources', icon: 'resources' },
  { label: 'My Bookings', path: '/my-bookings', icon: 'bookings' },
];

const APPROVALS_NAV_ITEM: NavItem = { label: 'Approvals', path: '/approvals', icon: 'approvals' };

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, BrandMarkComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent {
  // inject() instead of constructor parameters: field initializers below run
  // in declaration order, before the constructor body ever executes, so
  // `title`'s initializer needs `router`/`activatedRoute` already assigned by
  // the time it runs. A constructor-parameter property isn't assigned until
  // the constructor body does — which is exactly the ordering bug the
  // `formBuilder` fix in LoginComponent worked around a different way.
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  // A signal, not a plain array: it has to react to who's actually logged
  // in. "Approvals" only belongs in the list for an eligible approver
  // (AuthService.canApproveBookings) — everyone else never sees a link to a
  // route they'd just be bounced out of by approverGuard anyway.
  protected readonly primaryNavItems = computed<NavItem[]>(() =>
    this.auth.canApproveBookings() ? [...BASE_PRIMARY_NAV_ITEMS, APPROVALS_NAV_ITEM] : BASE_PRIMARY_NAV_ITEMS,
  );

  protected readonly secondaryNavItems: NavItem[] = [
    { label: 'Settings', path: '/settings', icon: 'settings' },
    { label: 'Help', path: '/help', icon: 'help' },
  ];

  // The active route's own `data.title` (set per-route in app.routes.ts),
  // kept live via router navigation events — a plain read on construction
  // would only ever show whichever page loaded first. toSignal subscribes to
  // the Observable and unsubscribes automatically when this component is
  // destroyed; no manual subscribe()/unsubscribe() to manage.
  protected readonly title = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.currentRouteTitle()),
    ),
    { initialValue: this.currentRouteTitle() },
  );

  // One crumb per level of the *actual* route tree — right now that's just
  // the current tab, since every route today is a flat, top-level sibling.
  // Once WP-7 adds real nesting (e.g. a Rooms page under Resources), this
  // needs to walk every matched level and collect each one's own title
  // (Resources -> Rooms), not synthesize a "Home" ancestor no route has.
  protected readonly breadcrumb = computed(() => [this.title()]);

  protected async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  // Walking router.routerState.snapshot (the fully-resolved tree the router
  // already computed for the current navigation) rather than the *live*
  // ActivatedRoute objects via this.activatedRoute.firstChild — the live tree
  // isn't fully wired up yet at the exact moment this runs during
  // ShellComponent's own construction, which is what crashed here first.
  private currentRouteTitle(): string {
    let snapshot = this.router.routerState.snapshot.root;
    while (snapshot.firstChild) {
      snapshot = snapshot.firstChild;
    }
    return (snapshot.data['title'] as string | undefined) ?? '';
  }
}
