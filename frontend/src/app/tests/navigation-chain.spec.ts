import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../app.routes';
import { buildFakeAccessToken } from '../core/auth/testing/jwt-fixture';

// WP-7 Phase 7 step 1 — the navigation chain, proven as a chain.
//
// **Every assertion here is about a seam**, never about a screen. Each screen's
// own behaviour is covered by its own spec; what nothing covered until now is
// whether the link one screen *renders* lands on a screen that *loads*. Those
// are different failures, and the second is the one a member hits: a booking
// that works when you type its URL and 404s when you click through to it is
// exactly the bug decision `0027` was written for, and it survived every
// screen-level test in the suite.
//
// **The rule that makes this a chain rather than seven isolated navigations:**
// each step reads the `href` the previous screen actually rendered and follows
// *that*. A test that navigated to a URL it built itself would prove the route
// config resolves — which `app.routes.spec.ts` already does — and would have
// been just as green while the queue linked members into a 404.
//
// Phase 4's TestBed gotcha applies throughout and is why every fetch is flushed
// rather than ignored: a spec that leaves a request open fails `verify()` and
// corrupts the shared TestBed for every spec file after it, showing up as
// unrelated failures that vary run to run.

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const API = 'http://localhost:5270';

const SESSIONS = {
  Member: { sub: 'u1', email: 'member1@acme.test' },
  Approver: { sub: 'approver-1', email: 'approver@acme.test' },
  // Admin console phase 2.
  TenantAdmin: { sub: 'admin-1', email: 'admin@acme.test' },
} as const;

function seedSession(role: keyof typeof SESSIONS): void {
  localStorage.setItem(
    'bookspace.accessToken',
    buildFakeAccessToken({ ...SESSIONS[role], orgId: 'org-1', [ROLE_CLAIM]: role }),
  );
  localStorage.setItem('bookspace.refreshToken', 'refresh-1');
}

// Instants built from local components, not "…Z" literals: these screens read
// in the viewer's zone, CI runs in UTC and local development here is CET.
function localInstant(y: number, m: number, d: number, h = 0, min = 0): string {
  return new Date(y, m, d, h, min).toISOString();
}

function resourceSummary(overrides: Record<string, unknown> = {}) {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: 'A room',
    resourceType: 'Room',
    capacity: 4,
    timeZoneId: 'UTC',
    requiresApproval: false,
    isArchived: false,
    ...overrides,
  };
}

function resourceDetail(overrides: Record<string, unknown> = {}) {
  return {
    ...resourceSummary(),
    minDurationMinutes: null,
    maxDurationMinutes: null,
    availabilityWindows: [],
    approvers: [],
    ...overrides,
  };
}

function bookingSummary(overrides: Record<string, unknown> = {}) {
  return {
    id: 'b1',
    resourceId: 'r1',
    resourceName: 'Conference Room A',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: localInstant(2026, 8, 24, 9, 0),
    endsAtUtc: localInstant(2026, 8, 24, 10, 0),
    quantity: 1,
    title: null,
    status: 'Confirmed',
    createdAtUtc: localInstant(2026, 8, 20, 8, 0),
    ...overrides,
  };
}

function bookingDetail(overrides: Record<string, unknown> = {}) {
  return {
    ...bookingSummary(),
    checkedInAtUtc: null,
    cancelledByUserId: null,
    cancelledAtUtc: null,
    cancellationReason: null,
    updatedAtUtc: localInstant(2026, 8, 20, 8, 0),
    approval: null,
    ...overrides,
  };
}

function pageOf(items: unknown[], overrides: Record<string, unknown> = {}) {
  return {
    items,
    page: 1,
    pageSize: 100,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
    ...overrides,
  };
}

describe('navigation chain (WP-7 Phase 7 step 1)', () => {
  let httpMock: HttpTestingController;
  let harness: RouterTestingHarness;
  let router: Router;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  // **`verify()` runs inside a `finally` that always resets the TestBed**, and
  // that is not defensive tidiness — it is this file refusing to be contagious.
  //
  // Writing this spec proved the gotcha Phase 4 recorded, from the inside: one
  // unflushed request (the availability screen's blackout fetch, missed below)
  // failed `verify()`, which threw out of `afterEach` before anything could
  // reset the module — and every subsequent test *in every other spec file*
  // then failed with "Cannot configure the test module when the test module has
  // already been instantiated". One missing line here produced 39 failures
  // across the suite, none of them in the file at fault.
  //
  // With the reset in a `finally`, a genuine leak still fails this file loudly
  // and stops there.
  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      TestBed.resetTestingModule();
      localStorage.clear();
    }
  });

  function dom(): HTMLElement {
    harness.detectChanges();
    return harness.fixture.nativeElement as HTMLElement;
  }

  // The whole point of the file: follow what is on screen, not a URL we made up.
  // An absent link fails here with a message naming the selector, which is the
  // failure a broken seam should produce.
  function hrefOf(selector: string): string {
    const el = dom().querySelector(selector);
    if (el === null) {
      throw new Error(`No element matched "${selector}" — the previous screen rendered no link.`);
    }
    const href = el.getAttribute('href');
    if (href === null) {
      throw new Error(`"${selector}" is not a real link — it has no href to follow.`);
    }
    return href;
  }

  async function follow(selector: string): Promise<string> {
    const href = hrefOf(selector);
    await harness.navigateByUrl(href);
    harness.detectChanges();
    return href;
  }

  function click(selector: string): void {
    const el = dom().querySelector<HTMLElement>(selector);
    if (el === null) {
      throw new Error(`No element matched "${selector}".`);
    }
    el.click();
    harness.detectChanges();
  }

  function flushResourceList(items: unknown[] = [resourceSummary()]): void {
    httpMock.expectOne((r) => r.url === `${API}/resources`).flush(pageOf(items));
  }

  function flushResource(id = 'r1', body = resourceDetail()): void {
    httpMock.expectOne(`${API}/resources/${id}`).flush(body);
  }

  // The directory's own load. Matched on the url alone, like the resource list
  // above, because `scope=All` is the component's business and is asserted in
  // its own spec — what this file is about is the link that got here.
  function flushUserDirectory(items: unknown[] = []): void {
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(pageOf(items));
  }

  describe("the member's path", () => {
    beforeEach(() => seedSession('Member'));

    it('walks resources → resource → availability → booking form, following only rendered links', async () => {
      harness = await RouterTestingHarness.create('/resources');
      flushResourceList();

      // The card's title is a real anchor (the accessibility fix of
      // 2026-09-16 made it one), so it is followable rather than a div with a
      // click handler.
      const toDetail = await follow('.card-title-link');
      expect(toDetail).toBe('/resources/r1');
      flushResource();
      expect(router.url).toBe('/resources/r1');

      // The detail screen's own CTA into availability. Its href is what the
      // archived-resource rule swaps out, so following it rather than building
      // it is what keeps this honest.
      const toAvailability = await follow('a[href="/resources/r1/availability"]');
      expect(toAvailability).toBe('/resources/r1/availability');
      flushResource();
      httpMock
        .expectOne((r) => r.url === `${API}/resources/r1/availability`)
        .flush({
          resourceId: 'r1',
          timeZoneId: 'UTC',
          fromLocalDate: '2026-09-24',
          toLocalDate: '2026-09-24',
          quantity: 1,
          isArchived: false,
          intervals: [
            { startUtc: '2026-09-24T08:00:00Z', endUtc: '2026-09-24T12:00:00Z', remainingCapacity: 4 },
          ],
        });

      // The availability screen makes a *third* request — the blackout fetch
      // that labels blocked spans. It is best-effort on that screen and easy to
      // forget here; forgetting it is what proved the contagion documented on
      // `afterEach` above.
      httpMock
        .expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`)
        .flush(pageOf([]));

      expect(router.url).toBe('/resources/r1/availability');
      expect(dom().querySelector('button.segment')).not.toBeNull();
    });

    it('reaches a booking from a calendar chip, and the chip is a real link', async () => {
      harness = await RouterTestingHarness.create('/calendar');
      httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(pageOf([bookingSummary()]));

      const toBooking = await follow('.chip, .week-chip');
      expect(toBooking).toBe('/bookings/b1');

      httpMock.expectOne(`${API}/bookings/b1`).flush(bookingDetail());
      flushResource();

      expect(router.url).toBe('/bookings/b1');
      expect(dom().textContent).toContain('Conference Room A');
    });

    // `/home` was the landing route for two work packages. Any link or bookmark
    // written before 2026-09-18 still has to resolve rather than falling through
    // to the catch-all and bouncing the visitor to /login.
    it('still resolves the retired /home bookmark onto the calendar', async () => {
      harness = await RouterTestingHarness.create('/home');
      httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(pageOf([]));

      expect(router.url).toBe('/calendar');
    });

    // The screen was removed in Phase 4, not repointed, so a stale link has no
    // route of its own. **Where it ends up is better than expected and worth
    // pinning**: the catch-all sends it to `/login`, `guestOnlyGuard` sees a
    // live session and bounces it to `/calendar` — so a signed-in member
    // following a link written before 2026-09-18 lands on the screen that
    // replaced the one they were asking for, rather than on a sign-in form they
    // do not need.
    //
    // This test was written expecting `/login` and was wrong; the app's actual
    // behaviour is the one worth keeping, so the assertion moved rather than the
    // routing.
    it('lands a stale /my-bookings link on the calendar, not on a sign-in form', async () => {
      harness = await RouterTestingHarness.create('/my-bookings');
      httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(pageOf([]));

      expect(router.url).not.toContain('my-bookings');
      expect(router.url).toBe('/calendar');
    });

    // The nav is the only way to most of this app, and it is rendered by the
    // shell rather than by any screen — so a nav item pointing at a route that
    // no longer exists would be invisible to every screen-level spec.
    it('renders only nav links that resolve', async () => {
      harness = await RouterTestingHarness.create('/settings');

      const navHrefs = Array.from(dom().querySelectorAll('nav a'))
        .map((a) => a.getAttribute('href'))
        .filter((h): h is string => h !== null);

      expect(navHrefs.length).toBeGreaterThan(0);
      for (const href of navHrefs) {
        expect(router.parseUrl(href).root.children['primary']).toBeDefined();
      }
    });

    // A member must not be offered the approvals route at all — approverGuard
    // bounces them, and the nav should not have shown it in the first place.
    it('hides Approvals from a member, and bounces them off the route', async () => {
      harness = await RouterTestingHarness.create('/settings');

      const navHrefs = Array.from(dom().querySelectorAll('nav a')).map((a) => a.getAttribute('href'));
      expect(navHrefs).not.toContain('/approvals');

      await harness.navigateByUrl('/approvals');
      httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(pageOf([]));

      expect(router.url).toBe('/calendar');
    });
  });

  describe("the approver's path", () => {
    beforeEach(() => seedSession('Approver'));

    it('walks the nav item → the queue → a request, following only rendered links', async () => {
      harness = await RouterTestingHarness.create('/settings');

      // The nav item exists for an approver and is the entry point — found by
      // its rendered href, not assumed.
      const toApprovals = await follow('nav a[href="/approvals"]');
      expect(toApprovals).toBe('/approvals');

      httpMock
        .expectOne((r) => r.url === `${API}/bookings` && r.params.get('scope') === 'tenant')
        .flush(pageOf([bookingSummary({ id: 'b9', status: 'Pending', userName: 'Member Two' })]));

      expect(router.url).toBe('/approvals');

      // The seam decision 0027 exists for: an approver clicking a queue row
      // used to land on a 404. Following the rendered href is the only version
      // of this test that would have caught it.
      const toBooking = await follow('.queue-card a.detail-link');
      expect(toBooking).toBe('/bookings/b9');

      httpMock
        .expectOne(`${API}/bookings/b9`)
        .flush(bookingDetail({ id: 'b9', status: 'Pending', userName: 'Member Two' }));
      flushResource();

      expect(router.url).toBe('/bookings/b9');
      expect(dom().textContent).toContain('Requested by');
    });

    // The return leg. An approver's "back" is the queue, not a calendar that
    // does not contain the booking — the other half of the same audience fix.
    it('sends the approver back to the queue from a booking they do not own', async () => {
      harness = await RouterTestingHarness.create('/bookings/b9');
      httpMock
        .expectOne(`${API}/bookings/b9`)
        .flush(bookingDetail({ id: 'b9', status: 'Pending' }));
      flushResource();

      const back = await follow('.detail-actions a');
      expect(back).toBe('/approvals');

      httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(pageOf([]));
      expect(router.url).toBe('/approvals');
    });
  });

  // Admin console phase 2. The console's entry seam, tested the same way as
  // every other one here: follow the href the shell actually rendered.
  //
  // This is the assertion the shell's own spec cannot make. That one reads
  // `primaryNavItems()`, a signal — which stays green if the nav item exists in
  // the array but the template never renders it, which is precisely how a
  // missing `@case` in the icon switch or a mistyped `routerLink` would fail.
  // WP-7 shipped two bugs of exactly that shape, both found by the owner
  // clicking rather than by the suite.
  describe("the administrator's path", () => {
    beforeEach(() => seedSession('TenantAdmin'));

    // Starts on /settings rather than the calendar deliberately: that route
    // fetches nothing, so this test is about the nav seam and not about
    // answering a calendar window request that has no bearing on it.
    it('renders an Admin link in the shell and follows it into the console', async () => {
      harness = await RouterTestingHarness.create('/settings');

      const toAdmin = await follow('a[href="/admin/resources"]');

      expect(toAdmin).toBe('/admin/resources');
      expect(router.url).toBe('/admin/resources');
      flushResourceList();
    });

    // Phase 3's two seams, each followed from the link the previous screen
    // rendered. The row title has to lead to the *admin* form — a row that led
    // to the member-facing detail screen would be a detour on every single use,
    // and only a rendered-href test notices which one it is.
    it('walks the console into the create form and into a resource', async () => {
      harness = await RouterTestingHarness.create('/admin/resources');
      flushResourceList();

      const toNew = await follow('a[href="/admin/resources/new"]');
      expect(toNew).toBe('/admin/resources/new');
      expect(router.url).toBe('/admin/resources/new');

      // Back to the list, then into the row itself.
      await harness.navigateByUrl('/admin/resources');
      flushResourceList();

      const toResource = await follow('.row-title-link');
      expect(toResource).toBe('/admin/resources/r1');
      flushResource();
      expect(router.url).toBe('/admin/resources/r1');

      // Phase 4's editor is reachable only from the resource form, so this is
      // the only seam it has — and the one a typo in the routerLink array would
      // break silently, since the route itself resolves either way.
      const toWindows = await follow('a[href="/admin/resources/r1/availability-windows"]');
      expect(toWindows).toBe('/admin/resources/r1/availability-windows');
      flushResource();
      expect(router.url).toBe('/admin/resources/r1/availability-windows');

      // Back to the resource, then into phase 5's picker. It reads *two*
      // endpoints, so both have to be answered or the spec leaves one open and
      // poisons every file after it.
      await harness.navigateByUrl('/admin/resources/r1');
      flushResource();

      const toApprovers = await follow('a[href="/admin/resources/r1/approvers"]');
      expect(toApprovers).toBe('/admin/resources/r1/approvers');
      flushResource();
      httpMock
        .expectOne((r) => r.url === `${API}/users`)
        .flush(pageOf([], { pageSize: 50 }));
      expect(router.url).toBe('/admin/resources/r1/approvers');

      // And phase 6, the last of the three per-resource admin screens.
      await harness.navigateByUrl('/admin/resources/r1');
      flushResource();

      const toBlackouts = await follow('a[href="/admin/resources/r1/blackout-periods"]');
      expect(toBlackouts).toBe('/admin/resources/r1/blackout-periods');
      flushResource();
      httpMock
        .expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`)
        .flush(pageOf([], { pageSize: 100 }));
      expect(router.url).toBe('/admin/resources/r1/blackout-periods');
    });

    // User management phase 6. The directory is the second admin entry point
    // and the first that is not reached through a resource, so its nav seam is
    // new rather than a variation on the one above — and the invite form is
    // reached only from the directory's own link.
    it('renders a Users link in the shell and follows it to the invite form', async () => {
      harness = await RouterTestingHarness.create('/settings');

      const toUsers = await follow('a[href="/admin/users"]');
      expect(toUsers).toBe('/admin/users');
      expect(router.url).toBe('/admin/users');
      flushUserDirectory();

      const toInvite = await follow('a[href="/admin/users/new"]');
      expect(toInvite).toBe('/admin/users/new');
      expect(router.url).toBe('/admin/users/new');
    });

    // The invite form's own way back, and the reason it is an anchor rather
    // than a button: admin console phase 7 found all three editors' return legs
    // were buttons, which this file cannot follow and which no component spec
    // is about.
    it('follows the invite form back to the directory', async () => {
      harness = await RouterTestingHarness.create('/admin/users/new');

      const back = await follow('a[href="/admin/users"]');
      expect(back).toBe('/admin/users');
      expect(router.url).toBe('/admin/users');
      flushUserDirectory();
    });

    // User management phase 7. The directory's rows became real links in this
    // phase — before it, following one was the assertion admin console phase 3
    // made admin-user-list.component.spec.ts stop short of on purpose, the same
    // way the approvers link waited for its own screen. This walks the whole
    // loop, directory → person → directory, the same shape phase 7's own
    // "walks back out of each editor" test uses for the per-resource editors.
    function directoryUser(overrides: Record<string, unknown> = {}) {
      return {
        id: 'u1',
        fullName: 'Member One',
        email: 'member1@acme.test',
        isActive: true,
        roles: ['Member'],
        ...overrides,
      };
    }

    it('walks the directory into a person and back', async () => {
      harness = await RouterTestingHarness.create('/admin/users');
      flushUserDirectory([directoryUser()]);

      const toPerson = await follow('.row-title-link');
      expect(toPerson).toBe('/admin/users/u1');

      httpMock.expectOne(`${API}/users/u1`).flush({
        ...directoryUser(),
        createdAtUtc: '2026-09-01T09:00:00Z',
        updatedAtUtc: '2026-09-01T09:00:00Z',
      });
      expect(router.url).toBe('/admin/users/u1');
      expect(dom().textContent).toContain('Member One');

      // The screen's own way back — an anchor, not a button, for the same
      // reason the per-resource editors' return legs had to become one.
      const back = await follow('.inline-link');
      expect(back).toBe('/admin/users');
      flushUserDirectory([directoryUser()]);
      expect(router.url).toBe('/admin/users');
    });

    // **Regression, phase 3.** The admin routes were briefly declared as a
    // componentless `resources` group nested under `admin`. Angular's default
    // `paramsInheritanceStrategy` ('emptyOnly') copies a parent's `data` onto
    // any child with an empty path *or no component*, so that group inherited
    // `title: 'Admin'` and the breadcrumb read "Admin > Admin > Resources" —
    // and then crashed the whole shell with NG0955, because the crumb loop
    // tracked by the crumb's own text and two of them were now identical.
    //
    // Both halves are fixed (flat sibling routes; tracking by position), and
    // this asserts the rendered crumbs rather than the route config, because
    // the route config is exactly what looked correct.
    it('renders one Admin crumb, not two', async () => {
      harness = await RouterTestingHarness.create('/admin/resources');
      flushResourceList();
      harness.detectChanges();

      const crumbs = (harness.fixture.nativeElement as HTMLElement).querySelector('.breadcrumb');
      const text = crumbs?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

      expect(text).toBe('Admin > Resources');
    });

    // The other half, and the one that matters for a role-gated link: a Member
    // is not merely bounced by the guard, they are never shown the way in.
    it('renders no Admin link at all for a member', async () => {
      seedSession('Member');
      harness = await RouterTestingHarness.create('/settings');
      harness.detectChanges();

      const shell = harness.fixture.nativeElement as HTMLElement;
      expect(shell.querySelector('a[href="/admin/resources"]')).toBeNull();
      expect(shell.querySelector('a[href="/resources"]')).not.toBeNull();
    });

    // **Admin console phase 7 — the return legs, and this is what found them.**
    //
    // All three per-resource editors are reachable *only* from the resource
    // form, so their way back is the one navigation each of them has. Until
    // phase 7 every one was a `<button (click)="goToResource()">` — which
    // works when clicked, cannot be ctrl-clicked or opened in a new tab, and
    // cannot be *followed* here, because `hrefOf` has nothing to read. The
    // chain could not be walked back, and nothing in the suite said so: each
    // editor's own spec asserted the component, and no component's spec is
    // about where the next screen is.
    //
    // They are real anchors now, and this walks the whole loop rather than
    // each hop in isolation — resource → editor → resource, three times —
    // because a link that goes back to the *list* instead of the resource
    // also passes a "does it navigate" test while costing two extra hops on
    // every single use.
    it('walks back out of each editor onto the resource, not the list', async () => {
      harness = await RouterTestingHarness.create('/admin/resources/r1');
      flushResource();

      const editors: [string, () => void][] = [
        ['/admin/resources/r1/availability-windows', () => flushResource()],
        [
          '/admin/resources/r1/approvers',
          () => {
            flushResource();
            httpMock.expectOne((r) => r.url === `${API}/users`).flush(pageOf([], { pageSize: 50 }));
          },
        ],
        [
          '/admin/resources/r1/blackout-periods',
          () => {
            flushResource();
            httpMock
              .expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`)
              .flush(pageOf([], { pageSize: 100 }));
          },
        ],
      ];

      for (const [href, flushEditor] of editors) {
        await follow(`a[href="${href}"]`);
        flushEditor();
        expect(router.url).toBe(href);

        // The return leg, read off the editor rather than built here. Before
        // phase 7 this threw — the element existed and was a `<button>`.
        const back = await follow('a[href="/admin/resources/r1"]');
        expect(back).toBe('/admin/resources/r1');
        flushResource();
        expect(router.url).toBe('/admin/resources/r1');
      }
    });

    // Decision `0028`'s loose end, as a seam. A gated resource with no
    // approvers renders a warning, and phase 5 gave that warning a link — it
    // deliberately pointed nowhere in phases 3 and 4, because the screen did
    // not exist yet and WP-7 Phase 6 had already taught this project what
    // linking into a 404 costs. This asserts the link is there *and* that it
    // resolves, which is the pair that matters.
    it('follows the gated-without-approvers warning onto the approvers screen', async () => {
      harness = await RouterTestingHarness.create('/admin/resources/r1');
      flushResource('r1', resourceDetail({ requiresApproval: true, approvers: [] }));

      const toApprovers = await follow('.field-warning a.inline-link');
      expect(toApprovers).toBe('/admin/resources/r1/approvers');

      flushResource('r1', resourceDetail({ requiresApproval: true, approvers: [] }));
      httpMock.expectOne((r) => r.url === `${API}/users`).flush(pageOf([], { pageSize: 50 }));

      expect(router.url).toBe('/admin/resources/r1/approvers');
    });

    // The console has one entrance and the guard is on the parent, so a role
    // that should not be here is bounced from *every* screen in it, not only
    // the list. An Approver is the interesting case rather than a Member:
    // they hold a privileged role, have their own nav item, and are the most
    // likely person to try the URL.
    it('bounces an approver off every admin screen, not just the entrance', async () => {
      seedSession('Approver');
      harness = await RouterTestingHarness.create('/settings');

      for (const url of [
        '/admin/resources',
        '/admin/resources/new',
        '/admin/resources/r1',
        '/admin/resources/r1/availability-windows',
        '/admin/resources/r1/approvers',
        '/admin/resources/r1/blackout-periods',
        // User management phases 6 and 7 — the console's guard is on the
        // parent route, so these two are bounced the same way rather than
        // needing their own coverage, and this proves it rather than assuming
        // it from the route tree.
        '/admin/users',
        '/admin/users/new',
        '/admin/users/u1',
      ]) {
        await harness.navigateByUrl(url);

        // The bounce lands on the calendar, which fetches its window — but
        // only when it is newly created, so the count is 1 on the first
        // iteration and 0 on the rest. `match` rather than `expectOne`
        // because what is asserted here is where the approver ends up, not
        // how many times the calendar re-instantiates on the way.
        for (const req of httpMock.match((r) => r.url === `${API}/bookings`)) {
          req.flush(pageOf([]));
        }

        expect(router.url).toBe('/calendar');
      }
    });
  });
});
