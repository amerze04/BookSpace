import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, ActivatedRouteSnapshot, NavigationEnd, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { ShellComponent } from '../components/shell/shell.component';
import { BreadcrumbService } from '../breadcrumb.service';
import { buildFakeAccessToken } from '../../core/auth/testing/jwt-fixture';

// The signals this spec drives are `protected` at compile time only — same
// widened-type pattern every other component spec in this app uses.
type TestableShellComponent = ShellComponent & {
  breadcrumb: () => string[];
  title: () => string;
  primaryNavItems: () => Array<{ label: string; path: string }>;
};

// Builds a linked list of bare ActivatedRouteSnapshot-shaped objects — only
// `data` and `firstChild` matter to ShellComponent's own walk, so nothing
// else needs to be real.
function fakeSnapshotChain(levels: Array<{ title?: string }>): ActivatedRouteSnapshot {
  let node: Partial<ActivatedRouteSnapshot> | null = null;
  for (let i = levels.length - 1; i >= 0; i--) {
    node = { data: levels[i].title ? { title: levels[i].title } : {}, firstChild: node as ActivatedRouteSnapshot | null };
  }
  return (node ?? { data: {}, firstChild: null }) as ActivatedRouteSnapshot;
}

describe('ShellComponent breadcrumb', () => {
  let events$: Subject<NavigationEnd>;
  let routerStub: {
    events: Subject<NavigationEnd>;
    routerState: { snapshot: { root: ActivatedRouteSnapshot } };
    navigateByUrl: () => Promise<boolean>;
  };
  let breadcrumbService: BreadcrumbService;

  function createComponent(levels: Array<{ title?: string }>): TestableShellComponent {
    events$ = new Subject();
    routerStub = {
      events: events$,
      routerState: { snapshot: { root: fakeSnapshotChain(levels) } },
      navigateByUrl: () => Promise.resolve(true),
    };

    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: routerStub },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    breadcrumbService = TestBed.inject(BreadcrumbService);
    return TestBed.createComponent(ShellComponent).componentInstance as TestableShellComponent;
  }

  afterEach(() => {
    breadcrumbService.setOverride(null);
    breadcrumbService.setInsertBeforeLast(null);
  });

  it('shows a single crumb for a flat, top-level route', () => {
    const component = createComponent([{ title: 'Resources' }]);

    expect(component.breadcrumb()).toEqual(['Resources']);
    expect(component.title()).toBe('Resources');
  });

  it('walks every matched level that carries a title, skipping a parent that has none', () => {
    // The real shape once WP-7 nested `resources` -> `:id`: the parent
    // `resources` route carries no title of its own, only its children do.
    const component = createComponent([{}, { title: 'Resources' }, { title: 'Resource details' }]);

    expect(component.breadcrumb()).toEqual(['Resources', 'Resource details']);
    expect(component.title()).toBe('Resource details');
  });

  it('re-derives the chain on every NavigationEnd rather than only at construction', () => {
    const component = createComponent([{ title: 'Resources' }]);
    expect(component.breadcrumb()).toEqual(['Resources']);

    routerStub.routerState.snapshot.root = fakeSnapshotChain([{ title: 'My Bookings' }]);
    events$.next(new NavigationEnd(1, '/my-bookings', '/my-bookings'));

    expect(component.breadcrumb()).toEqual(['My Bookings']);
  });

  it('replaces only the last crumb with the BreadcrumbService override', () => {
    const component = createComponent([{}, { title: 'Resources' }, { title: 'Resource details' }]);

    breadcrumbService.setOverride('Conference Room A');

    expect(component.breadcrumb()).toEqual(['Resources', 'Conference Room A']);
    expect(component.title()).toBe('Conference Room A');
  });

  it('falls back to the route title again once the override is cleared', () => {
    const component = createComponent([{ title: 'Resources' }, { title: 'Resource details' }]);
    breadcrumbService.setOverride('Conference Room A');
    expect(component.breadcrumb()).toEqual(['Resources', 'Conference Room A']);

    breadcrumbService.setOverride(null);

    expect(component.breadcrumb()).toEqual(['Resources', 'Resource details']);
  });

  it('ignores a leftover override on a route with no crumbs at all', () => {
    // Defensive case: an override with nothing to replace must not conjure
    // a crumb out of nowhere.
    const component = createComponent([]);
    breadcrumbService.setOverride('Conference Room A');

    expect(component.breadcrumb()).toEqual([]);
  });

  it('inserts insertBeforeLast just before the last crumb, without replacing it', () => {
    // The availability screen's own case: its route chain only ever
    // contributes ['Resources', 'Availability'] (a sibling of `:id`, not a
    // child of it), so there is no 'Resource details' crumb to override —
    // the resource name has to be inserted instead.
    const component = createComponent([{ title: 'Resources' }, { title: 'Availability' }]);

    breadcrumbService.setInsertBeforeLast('Conference Room A');

    expect(component.breadcrumb()).toEqual(['Resources', 'Conference Room A', 'Availability']);
    expect(component.title()).toBe('Availability');
  });

  it('applies insertBeforeLast after override, so both can compose', () => {
    const component = createComponent([{ title: 'Resources' }, { title: 'Resource details' }]);

    breadcrumbService.setOverride('Not applicable here');
    breadcrumbService.setInsertBeforeLast('Conference Room A');

    // override replaces the last crumb first ('Resource details' ->
    // 'Not applicable here'), then insertBeforeLast splices in ahead of
    // whatever is now last — exercised together only to prove the two
    // signals don't clobber each other, not because a real screen combines
    // them this way.
    expect(component.breadcrumb()).toEqual(['Resources', 'Conference Room A', 'Not applicable here']);
  });

  it('ignores a leftover insertBeforeLast on a route with no crumbs at all', () => {
    const component = createComponent([]);
    breadcrumbService.setInsertBeforeLast('Conference Room A');

    expect(component.breadcrumb()).toEqual([]);
  });
});

// Admin console phase 2. The nav is where the role plumbing becomes visible,
// and it is the half a guard cannot cover: adminGuard stops someone reaching
// /admin by typing it, and this is what stops them being *invited* to.
//
// Driven through real (fake-signed) tokens in localStorage rather than a
// stubbed AuthService, because what is actually under test is the chain
// token -> decodeAccessToken -> roles -> isTenantAdmin -> nav item. A stub
// would skip the two steps most likely to be wrong: the role claim's full URI
// key, and a single-role claim arriving as a string rather than an array.
describe('ShellComponent primary nav', () => {
  const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

  function seedSession(roles: string | string[]): void {
    localStorage.setItem(
      'bookspace.accessToken',
      buildFakeAccessToken({ sub: 'u1', email: 'a@acme.test', orgId: 'org-1', [ROLE_CLAIM]: roles }),
    );
    localStorage.setItem('bookspace.refreshToken', 'refresh-1');
  }

  function createShell(): TestableShellComponent {
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: Router,
          useValue: {
            events: new Subject<NavigationEnd>(),
            routerState: { snapshot: { root: { data: {}, firstChild: null } as ActivatedRouteSnapshot } },
            navigateByUrl: () => Promise.resolve(true),
          },
        },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    return TestBed.createComponent(ShellComponent).componentInstance as TestableShellComponent;
  }

  function labels(): string[] {
    return createShell()
      .primaryNavItems()
      .map((item) => item.label);
  }

  beforeEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('shows a Member only the two screens they can use', () => {
    seedSession('Member');

    expect(labels()).toEqual(['Calendar', 'Resources']);
  });

  // An Approver gets Approvals and must not get Admin — the distinction
  // adminGuard enforces, shown here at the point where it is visible.
  it('adds Approvals for an Approver, and nothing else', () => {
    seedSession('Approver');

    expect(labels()).toEqual(['Calendar', 'Resources', 'Approvals']);
  });

  // A TenantAdmin satisfies both predicates — canApproveBookings admits the
  // role (decision 0018: the set that may be assigned as an approver and the
  // set that may approve have to be the same one), and isTenantAdmin is what
  // adds the console.
  // 'Users' joined the list in user management phase 6 — the second admin entry
  // point, and the only administration screen that is not a sub-resource of
  // /resources/{id}. It sits after Admin so the two group together.
  it('gives a TenantAdmin Approvals and both admin entries, in that order', () => {
    seedSession('TenantAdmin');

    expect(labels()).toEqual(['Calendar', 'Resources', 'Approvals', 'Admin', 'Users']);
  });

  it('handles a multi-role claim arriving as an array', () => {
    seedSession(['Approver', 'TenantAdmin']);

    expect(labels()).toEqual(['Calendar', 'Resources', 'Approvals', 'Admin', 'Users']);
  });

  // Same rule as Admin, and worth its own assertion: the directory is
  // TenantAdmin-only end to end, so offering it to an Approver would be a link
  // to a screen whose every request answers 403.
  it('never offers Users to a plain Approver', () => {
    seedSession('Approver');

    expect(labels()).not.toContain('Users');
  });

  // The same rule adminGuard applies, at the other end: a SysAdmin has no
  // orgId claim, so every admin endpoint would refuse them. Offering the link
  // would be an invitation to a console that answers 403 throughout.
  it('never offers Admin to a SysAdmin', () => {
    seedSession('SysAdmin');

    expect(labels()).not.toContain('Admin');
  });

  it('shows no role-gated items at all when there is no session', () => {
    expect(labels()).toEqual(['Calendar', 'Resources']);
  });

  // The link has to point where the visitor actually lands. /admin redirects to
  // /admin/resources, and a nav item pointing at the redirect would leave
  // routerLinkActive comparing against a URL that no longer exists after the
  // hop — the same reason approverGuard targets /calendar rather than /home.
  it('points Admin straight at /admin/resources, not at the redirect', () => {
    seedSession('TenantAdmin');

    const admin = createShell()
      .primaryNavItems()
      .find((item) => item.label === 'Admin');

    expect(admin?.path).toBe('/admin/resources');
  });
});
