import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { AdminUserListComponent } from '../components/admin-user-list/admin-user-list.component';
import { PagedResult } from '../../../core/http/paged-result';
import { DirectoryUser } from '../models/users.models';

// User management phase 6, the directory.
//
// The assertion this file exists for is the first one: **it sends
// `scope=All`**. Without it the screen would silently render the
// eligible-approver set — two people in the seeded tenant instead of four — and
// look entirely plausible while being the wrong list. Nothing else would catch
// that: the rows render the same either way.

const API = 'http://localhost:5270';

type TestableList = AdminUserListComponent & {
  items: () => DirectoryUser[];
  loading: () => boolean;
  loadError: () => boolean;
  page: () => number;
  totalPages: () => number;
  searchText: () => string;
  onSearchInput(event: Event): void;
  goToNextPage(): void;
  goToPreviousPage(): void;
  retry(): void;
  sortedRoles(user: DirectoryUser): string[];
};

function fakeInputEvent(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function user(overrides: Partial<DirectoryUser> = {}): DirectoryUser {
  return {
    id: 'u1',
    fullName: 'Member One',
    email: 'member1@acme.test',
    isActive: true,
    roles: ['Member'],
    ...overrides,
  };
}

function page(items: DirectoryUser[], overrides: Partial<PagedResult<DirectoryUser>> = {}): PagedResult<DirectoryUser> {
  return {
    items,
    page: 1,
    pageSize: 50,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
    ...overrides,
  };
}

describe('AdminUserListComponent', () => {
  let httpMock: HttpTestingController;

  function create(): TestableList {
    TestBed.configureTestingModule({
      imports: [AdminUserListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
        // RouterLink injects ActivatedRoute as soon as a render pass runs,
        // whether or not a test asks for one.
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminUserListComponent).componentInstance as TestableList;
  }

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  function flushInitial(items: DirectoryUser[] = [user()]): void {
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(page(items));
  }

  // **The one that matters.** An omitted scope is the eligible-approver set,
  // which would render as a shorter but entirely plausible list.
  it('asks for every user in the tenant, not the eligible-approver set', () => {
    create();

    const request = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(request.request.params.get('scope')).toBe('All');
    expect(request.request.params.get('pageSize')).toBe('50');
    request.flush(page([user()]));
  });

  it('renders what came back', () => {
    const component = create();

    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush(page([user(), user({ id: 'u2', fullName: 'Tenant Admin', roles: ['TenantAdmin'] })]));

    expect(component.items()).toHaveLength(2);
    expect(component.loading()).toBe(false);
  });

  // The field the directory exists for: without it an administrator cannot tell
  // somebody who left from somebody who was never added.
  it('keeps a deactivated account in the list and marked as such', () => {
    const component = create();

    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush(page([user(), user({ id: 'u2', fullName: 'Gone Away', isActive: false })]));

    const deactivated = component.items().find((u) => u.id === 'u2');
    expect(deactivated?.isActive).toBe(false);
    expect(component.items()).toHaveLength(2);
  });

  it('shows a deactivated badge only for the inactive row', async () => {
    TestBed.configureTestingModule({
      imports: [AdminUserListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminUserListComponent);
    fixture.detectChanges();

    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush(page([user(), user({ id: 'u2', fullName: 'Gone Away', isActive: false })]));
    await fixture.whenStable();
    fixture.detectChanges();

    // Asserted against the rendered DOM rather than the signal: "is the badge
    // on screen" is the actual question, and a signal-level check would pass
    // with the template's condition inverted.
    const badges = fixture.nativeElement.querySelectorAll('.badge--inactive');
    expect(badges).toHaveLength(1);

    const rows = fixture.nativeElement.querySelectorAll('.user-row');
    expect(rows[0].classList.contains('user-row--inactive')).toBe(false);
    expect(rows[1].classList.contains('user-row--inactive')).toBe(true);
  });

  // Phase 7 adds the detail screen. Until it exists a row must not link
  // anywhere — the mistake admin console phase 3 avoided by leaving the
  // approvers link out until the screen was there.
  it('does not link a row anywhere yet', async () => {
    TestBed.configureTestingModule({
      imports: [AdminUserListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminUserListComponent);
    fixture.detectChanges();

    httpMock.expectOne((r) => r.url === `${API}/users`).flush(page([user()]));
    await fixture.whenStable();
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('.user-row');
    expect(row.querySelector('a')).toBeNull();
  });

  // A real router here, not the stub the other tests use: RouterLink can only
  // produce an `href` when it has one to ask, and the href is the whole
  // assertion — this is the seam `navigation-chain.spec.ts` follows.
  it('offers a link to the invite form', async () => {
    TestBed.configureTestingModule({
      imports: [AdminUserListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminUserListComponent);
    fixture.detectChanges();

    httpMock.expectOne((r) => r.url === `${API}/users`).flush(page([user()]));
    await fixture.whenStable();
    fixture.detectChanges();

    const action = fixture.nativeElement.querySelector('.primary-action');
    expect(action.getAttribute('href')).toBe('/admin/users/new');
  });

  it('searches server-side, on the same route', async () => {
    const component = create();
    flushInitial();

    component.onSearchInput(fakeInputEvent('ada'));
    await new Promise((resolve) => setTimeout(resolve, 350));

    const request = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(request.request.params.get('search')).toBe('ada');
    // Still the directory, not the picker — a search must not narrow the scope.
    expect(request.request.params.get('scope')).toBe('All');
    request.flush(page([user()]));
  });

  it('omits search entirely when the box is empty', () => {
    create();

    const request = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(request.request.params.has('search')).toBe(false);
    request.flush(page([]));
  });

  // Staying on page 3 of a filter that now has one page shows "nobody matches"
  // for a search with plenty of results.
  it('returns to page one when the search changes', async () => {
    const component = create();
    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush(page([user()], { totalPages: 3, hasNextPage: true }));

    component.goToNextPage();
    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush(page([user()], { page: 2, totalPages: 3, hasNextPage: true, hasPreviousPage: true }));
    expect(component.page()).toBe(2);

    component.onSearchInput(fakeInputEvent('ada'));
    await new Promise((resolve) => setTimeout(resolve, 350));

    const request = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(request.request.params.get('page')).toBe('1');
    request.flush(page([user()]));
    expect(component.page()).toBe(1);
  });

  it('shows an error state with a retry when the load fails', () => {
    const component = create();

    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush({}, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);
    expect(component.loading()).toBe(false);

    component.retry();
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(page([user()]));
    expect(component.loadError()).toBe(false);
  });

  // A stale response landing after a newer one must not overwrite it — paging
  // while a debounced search is in flight.
  it('ignores a response from a superseded request', () => {
    const component = create();
    const first = httpMock.expectOne((r) => r.url === `${API}/users`);

    component.retry();
    const second = httpMock.expectOne((r) => r.url === `${API}/users`);

    second.flush(page([user({ id: 'newest', fullName: 'Newest' })]));
    first.flush(page([user({ id: 'stale', fullName: 'Stale' })]));

    expect(component.items()[0].id).toBe('newest');
  });

  it('sorts a row’s roles so two people with the same roles read alike', () => {
    const component = create();
    flushInitial();

    expect(component.sortedRoles(user({ roles: ['TenantAdmin', 'Approver', 'Member'] }))).toEqual([
      'Approver',
      'Member',
      'TenantAdmin',
    ]);
  });
});
