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
import { AuthService } from '../../../core/auth/auth.service';
import { BrandMarkComponent } from '../../../shared/brand-mark/brand-mark.component';
import { BreadcrumbService } from '../../breadcrumb.service';

interface NavItem {
  label: string;
  path: string;
  icon: 'calendar' | 'resources' | 'approvals' | 'admin' | 'users' | 'settings' | 'help';
}

// WP-7 Phase 4 (2026-09-18): "Home" and "My Bookings" were both removed and
// replaced by one "Calendar" item. Home was a placeholder with no assigned job
// and now redirects here, so keeping it would have been a second link to the
// same page; My Bookings was cancelled outright, its screen made redundant by
// the calendar. The `home` and `bookings` icons went with them rather than
// being left as unreachable branches in the template's switch.
const BASE_PRIMARY_NAV_ITEMS: NavItem[] = [
  { label: 'Calendar', path: '/calendar', icon: 'calendar' },
  { label: 'Resources', path: '/resources', icon: 'resources' },
];

const APPROVALS_NAV_ITEM: NavItem = { label: 'Approvals', path: '/approvals', icon: 'approvals' };

// Admin console phase 2. One entry point, not a group: every other
// administration screen (availability windows, approvers, blackout periods) is
// reached *through* a resource, because that is how the endpoints are shaped —
// all of them are sub-resources of /resources/{id}. A flat list of four admin
// nav items would promise four destinations the API cannot address without a
// resource in hand.
//
// It points at /admin/resources rather than /admin so the active-link
// highlighting matches the URL the visitor actually lands on, the same reason
// approverGuard redirects to /calendar rather than through /home.
const ADMIN_NAV_ITEM: NavItem = { label: 'Admin', path: '/admin/resources', icon: 'admin' };

// User management phase 6. **The second admin entry point, and the exception
// the comment above describes rather than a contradiction of it.** That comment
// refuses a flat list of admin items because availability windows, approvers
// and blackout periods are all sub-resources of `/resources/{id}` — they cannot
// be addressed without a resource in hand, so a nav item for them would promise
// a destination the API has no URL for. `GET /users` and `POST /users` are
// top-level routes with no such dependency, so this one is reachable directly
// and belongs in the sidebar.
//
// It sits after Admin, so the two administration entries group together at the
// bottom of the list.
//
// The alternative — one "Admin" item leading to a tabbed console — is the
// tidier answer if this grows a third section, and is noted rather than built:
// two destinations do not need a tab strip and a shared layout route.
const ADMIN_USERS_NAV_ITEM: NavItem = { label: 'Users', path: '/admin/users', icon: 'users' };

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
  // route they'd just be bounced out of by approverGuard anyway. "Admin" works
  // the same way against adminGuard.
  //
  // The two are independent, and both orderings of the pair occur: a TenantAdmin
  // sees Approvals *and* Admin, a plain Approver sees only Approvals, and a
  // TenantAdmin who somehow held neither would still see Resources. Built by
  // appending in a fixed order rather than filtering a master list, so the
  // sequence is the one written here rather than an accident of predicate order.
  protected readonly primaryNavItems = computed<NavItem[]>(() => {
    const items = [...BASE_PRIMARY_NAV_ITEMS];

    if (this.auth.canApproveBookings()) {
      items.push(APPROVALS_NAV_ITEM);
    }

    if (this.auth.isTenantAdmin()) {
      items.push(ADMIN_NAV_ITEM);
      items.push(ADMIN_USERS_NAV_ITEM);
    }

    return items;
  });

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
  //
  // insertBeforeLast then splices a second, independent crumb in just before
  // whatever is now last — the availability screen's own case (see
  // BreadcrumbService's comment): its route chain never contributed a crumb
  // for the resource at all, so there is nothing here to *replace*, only
  // something to insert.
  protected readonly breadcrumb = computed(() => {
    const chain = this.routeTitleChain();
    const override = this.breadcrumbService.override();
    const insertBeforeLast = this.breadcrumbService.insertBeforeLast();

    const withOverride = override && chain.length > 0 ? [...chain.slice(0, -1), override] : chain;

    return insertBeforeLast && withOverride.length > 0
      ? [...withOverride.slice(0, -1), insertBeforeLast, withOverride[withOverride.length - 1]]
      : withOverride;
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
