import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ActivatedRouteSnapshot,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { filter, map } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { BrandMarkComponent } from '../../shared/brand-mark/brand-mark.component';
import { BreadcrumbService } from '../breadcrumb.service';

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
  private readonly breadcrumbService = inject(BreadcrumbService);

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

  // One title per matched level of the *actual* route tree, kept live via
  // router navigation events — a plain read on construction would only ever
  // show whichever page loaded first. toSignal subscribes to the Observable
  // and unsubscribes automatically when this component is destroyed; no
  // manual subscribe()/unsubscribe() to manage.
  //
  // Was a single title until WP-7 Phase 1 step 4, which is the first route
  // that's genuinely nested (`resources` -> `:id`) — see routeTitleChain.
  private readonly routeTitleChain = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.computeRouteTitleChain()),
    ),
    { initialValue: this.computeRouteTitleChain() },
  );

  // The route-config chain with its last segment swapped for
  // BreadcrumbService's override, when a leaf page has set one — a loaded
  // resource's own name is not something any static `data: { title }` could
  // carry. Falls back to the plain route chain before a page loads anything,
  // or on a route that never sets an override at all.
  protected readonly breadcrumb = computed(() => {
    const chain = this.routeTitleChain();
    const override = this.breadcrumbService.override();

    return override && chain.length > 0 ? [...chain.slice(0, -1), override] : chain;
  });

  // The page heading always matches the breadcrumb's last crumb — one
  // source for both, rather than two computations that could disagree.
  protected readonly title = computed(() => {
    const crumbs = this.breadcrumb();
    return crumbs[crumbs.length - 1] ?? '';
  });

  protected async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  // Walking router.routerState.snapshot (the fully-resolved tree the router
  // already computed for the current navigation) rather than the *live*
  // ActivatedRoute objects via this.activatedRoute.firstChild — the live tree
  // isn't fully wired up yet at the exact moment this runs during
  // ShellComponent's own construction, which is what crashed here first.
  //
  // Every matched level that carries its own `data.title` contributes one
  // crumb; a level with none (the parentless `resources` path itself, which
  // exists only to group its children) is skipped rather than appearing as a
  // blank crumb.
  private computeRouteTitleChain(): string[] {
    const titles: string[] = [];
    let snapshot: ActivatedRouteSnapshot | null = this.router.routerState.snapshot.root;

    while (snapshot) {
      const title = snapshot.data['title'] as string | undefined;
      if (title) {
        titles.push(title);
      }
      snapshot = snapshot.firstChild;
    }

    return titles;
  }
}
