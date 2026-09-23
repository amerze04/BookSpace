import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { AdminApproversComponent } from '../components/admin-approvers/admin-approvers.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { ApproverDetail, ResourceDetail } from '../../resources/models/resources.models';
import { EligibleUser } from '../models/users.models';
import { PagedResult } from '../../../core/http/paged-result';

const API = 'http://localhost:5270';

type TestableApprovers = AdminApproversComponent & {
  options: () => Array<{ userId: string; selected: boolean }>;
  stranded: () => Array<{ userId: string; fullName: string }>;
  selectedCount: () => number;
  dirty: () => boolean;
  canSave: () => boolean;
  saved: () => boolean;
  saving: () => boolean;
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  isArchived: () => boolean;
  gatedWithNobody: () => boolean;
  searchText: () => string;
  rejection: () => { formMessage: string | null } | null;
  toggle(userId: string): void;
  onSearchInput(event: Event): void;
  save(): void;
  retryLoad(): void;
};

function input(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function problem(status: number, reasonCode: string) {
  return { status, title: 'The request was refused.', reasonCode };
}

function user(overrides: Partial<EligibleUser> = {}): EligibleUser {
  return {
    id: 'u1',
    fullName: 'Resource Approver',
    email: 'approver@acme.test',
    roles: ['Approver'],
    ...overrides,
  };
}

function detail(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: null,
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'UTC',
    requiresApproval: false,
    minDurationMinutes: null,
    maxDurationMinutes: null,
    isArchived: false,
    createdAtUtc: '2026-09-01T08:00:00Z',
    updatedAtUtc: '2026-09-01T08:00:00Z',
    availabilityWindows: [],
    approvers: [],
    ...overrides,
  };
}

function usersPage(items: EligibleUser[], totalCount = items.length): PagedResult<EligibleUser> {
  return {
    items,
    page: 1,
    pageSize: 50,
    totalCount,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
  };
}

describe('AdminApproversComponent', () => {
  let httpMock: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  function create(): TestableApprovers {
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [AdminApproversComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminApproversComponent).componentInstance as TestableApprovers;
  }

  // Both reads land together — the ticks come from the resource and the
  // pickable set from /users, so neither is meaningful alone.
  function createLoaded(
    resource: ResourceDetail = detail(),
    users: EligibleUser[] = [user({ id: 'u1' }), user({ id: 'u2', fullName: 'Tenant Admin', roles: ['TenantAdmin'] })],
  ): TestableApprovers {
    const component = create();
    httpMock.expectOne(`${API}/resources/r1`).flush(resource);
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage(users));
    return component;
  }

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      const breadcrumbs = TestBed.inject(BreadcrumbService);
      breadcrumbs.setOverride(null);
      breadcrumbs.setInsertBeforeLast(null);
      TestBed.resetTestingModule();
    }
  });

  // ---- Loading ----

  it('reads the resource and the eligible set together', () => {
    const component = createLoaded();

    expect(component.loading()).toBe(false);
    expect(component.options()).toHaveLength(2);
  });

  it('ticks the people already assigned to the resource', () => {
    const component = createLoaded(detail({ approvers: [{ userId: 'u2', fullName: 'Tenant Admin' }] }));

    expect(component.options().find((o) => o.userId === 'u2')?.selected).toBe(true);
    expect(component.options().find((o) => o.userId === 'u1')?.selected).toBe(false);
    expect(component.selectedCount()).toBe(1);
  });

  it('inserts the resource name into the breadcrumb without replacing the last crumb', () => {
    createLoaded();

    expect(TestBed.inject(BreadcrumbService).insertBeforeLast()).toBe('Conference Room A');
  });

  // **The users request is answered first, deliberately.** `forkJoin` cancels
  // its remaining sources the instant one errors, so flushing the resource's
  // failure first would leave the /users request cancelled and unflushable —
  // which is a property of the operator worth knowing before writing any other
  // test against this screen.
  it('shows a not-found state for a resource that is not this tenant’s', () => {
    const component = create();
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage([]));
    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(404, 'ResourceNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
  });

  it('shows a retryable error when a read fails for another reason', () => {
    const component = create();
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage([]));
    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);

    component.retryLoad();
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage([user()]));
    expect(component.loadError()).toBe(false);
  });

  // ---- The two-endpoint gap ----

  // **The silent-removal bug this screen exists to avoid.** An approver
  // deactivated after being assigned still comes back on the resource read but
  // not from /users. Shown, not dropped.
  it('surfaces an assigned person the picker can no longer offer', () => {
    const component = createLoaded(
      detail({ approvers: [{ userId: 'u9', fullName: 'Since Deactivated' }] }),
      [user({ id: 'u1' })],
    );

    expect(component.stranded()).toEqual([{ userId: 'u9', fullName: 'Since Deactivated' }]);
  });

  // A searched or paged picker is showing a subset, so absence proves nothing —
  // somebody merely on another page is not stranded.
  it('does not call someone stranded merely because a search hid them', () => {
    vi.useFakeTimers();
    try {
      const component = createLoaded(
        detail({ approvers: [{ userId: 'u2', fullName: 'Tenant Admin' }] }),
        [user({ id: 'u1' }), user({ id: 'u2', fullName: 'Tenant Admin' })],
      );
      expect(component.stranded()).toEqual([]);

      component.onSearchInput(input('Resource'));
      vi.advanceTimersByTime(300);
      httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage([user({ id: 'u1' })], 1));

      // u2 is assigned and absent from the *searched* page, which proves
      // nothing — absence is only evidence when the picker is showing
      // everything.
      expect(component.stranded()).toEqual([]);
    } finally {
      vi.useRealTimers();
    }
  });

  // ---- Selecting ----

  it('toggles a person on and off, and tracks dirtiness against the server list', () => {
    const component = createLoaded();

    expect(component.dirty()).toBe(false);

    component.toggle('u1');
    expect(component.dirty()).toBe(true);
    expect(component.canSave()).toBe(true);

    component.toggle('u1');
    expect(component.dirty()).toBe(false);
    expect(component.canSave()).toBe(false);
  });

  it('will not save when nothing has changed', () => {
    const component = createLoaded();

    component.save();
    httpMock.expectNone(`${API}/resources/r1/approvers`);
  });

  // A person ticked and then searched away from is still assigned; losing them
  // because they scrolled out of view would be the same silent removal.
  it('keeps a selection when the picker is searched', () => {
    vi.useFakeTimers();
    try {
      const component = createLoaded();
      component.toggle('u2');

      component.onSearchInput(input('Resource'));
      vi.advanceTimersByTime(300);
      httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage([user({ id: 'u1' })], 1));

      expect(component.selectedCount()).toBe(1);
      expect(component.dirty()).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('searches server-side after a debounce', () => {
    vi.useFakeTimers();
    try {
      const component = createLoaded();

      component.onSearchInput(input('Ten'));
      httpMock.expectNone((r) => r.url === `${API}/users`);

      vi.advanceTimersByTime(300);
      const request = httpMock.expectOne((r) => r.url === `${API}/users`);
      expect(request.request.params.get('search')).toBe('Ten');
      request.flush(usersPage([user({ id: 'u2', fullName: 'Tenant Admin' })], 1));
    } finally {
      vi.useRealTimers();
    }
  });

  // ---- Saving ----

  it('sends bare sorted ids, replace-the-set', () => {
    const component = createLoaded();
    component.toggle('u2');
    component.toggle('u1');

    component.save();

    const request = httpMock.expectOne(`${API}/resources/r1/approvers`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ approverUserIds: ['u1', 'u2'] });

    request.flush({
      resourceId: 'r1',
      requiresApproval: false,
      approvers: [
        { userId: 'u1', fullName: 'Resource Approver' },
        { userId: 'u2', fullName: 'Tenant Admin' },
      ] as ApproverDetail[],
    });

    expect(component.saved()).toBe(true);
    expect(component.dirty()).toBe(false);
  });

  // Decision `0028`: clearing the list on a gated resource is accepted, and the
  // resource stays gated.
  it('sends an empty array to clear the list', () => {
    const component = createLoaded(
      detail({ requiresApproval: true, approvers: [{ userId: 'u1', fullName: 'Resource Approver' }] }),
    );
    component.toggle('u1');

    component.save();

    const request = httpMock.expectOne(`${API}/resources/r1/approvers`);
    expect(request.request.body).toEqual({ approverUserIds: [] });
    request.flush({ resourceId: 'r1', requiresApproval: true, approvers: [] });

    expect(component.selectedCount()).toBe(0);
    expect(component.saved()).toBe(true);
  });

  it('re-seeds from the response rather than from the selection', () => {
    const component = createLoaded();
    component.toggle('u1');
    component.save();

    httpMock.expectOne(`${API}/resources/r1/approvers`).flush({
      resourceId: 'r1',
      requiresApproval: false,
      approvers: [{ userId: 'u1', fullName: 'Renamed By Server' }] as ApproverDetail[],
    });

    expect(component.dirty()).toBe(false);
    expect(component.canSave()).toBe(false);
  });

  // ---- Decision 0028's state, reached from here ----

  it('warns when a gated resource would be left with nobody assigned', () => {
    const component = createLoaded(
      detail({ requiresApproval: true, approvers: [{ userId: 'u1', fullName: 'Resource Approver' }] }),
    );

    expect(component.gatedWithNobody()).toBe(false);

    component.toggle('u1');

    expect(component.gatedWithNobody()).toBe(true);
  });

  it('does not warn about an ungated resource with nobody assigned', () => {
    const component = createLoaded(detail({ requiresApproval: false }));

    expect(component.gatedWithNobody()).toBe(false);
  });

  // ---- Refusals ----

  // The picker only offers eligible people, so this means somebody changed
  // underneath. The message cannot say who or why — `0018` collapses the three
  // causes — so it says what is true and useful instead.
  it('explains an ineligible approver as something having changed since the page loaded', () => {
    const component = createLoaded();
    component.toggle('u1');
    component.save();

    httpMock
      .expectOne(`${API}/resources/r1/approvers`)
      .flush(problem(422, 'ApproverNotEligible'), { status: 422, statusText: 'Unprocessable' });

    expect(component.rejection()?.formMessage).toContain('no longer approve');
    expect(component.rejection()?.formMessage).toContain('Reload');
  });

  it('says an archived resource can no longer have its approvers changed', () => {
    const component = createLoaded();
    component.toggle('u1');
    component.save();

    httpMock
      .expectOne(`${API}/resources/r1/approvers`)
      .flush(problem(422, 'ResourceArchived'), { status: 422, statusText: 'Unprocessable' });

    expect(component.rejection()?.formMessage).toContain('archived');
  });

  it('clears a rejection as soon as the selection changes again', () => {
    const component = createLoaded();
    component.toggle('u1');
    component.save();
    httpMock
      .expectOne(`${API}/resources/r1/approvers`)
      .flush(problem(409, 'ConcurrencyConflict'), { status: 409, statusText: 'Conflict' });
    expect(component.rejection()).not.toBeNull();

    component.toggle('u2');

    expect(component.rejection()).toBeNull();
  });

  // ---- Archived ----

  it('is read-only for an archived resource', () => {
    const component = createLoaded(detail({ isArchived: true }));

    expect(component.isArchived()).toBe(true);

    component.toggle('u1');
    expect(component.canSave()).toBe(false);

    component.save();
    httpMock.expectNone(`${API}/resources/r1/approvers`);
  });

  // ---- Rendered output ----

  it('renders each person with the role that makes them eligible', () => {
    TestBed.configureTestingModule({
      imports: [AdminApproversComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(AdminApproversComponent);
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    httpMock
      .expectOne((r) => r.url === `${API}/users`)
      .flush(usersPage([user({ id: 'u2', fullName: 'Tenant Admin', email: 'admin@acme.test', roles: ['TenantAdmin'] })]));
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Tenant Admin');
    expect(text).toContain('admin@acme.test');
    expect(text).toContain('Administrator');
  });

  // An empty eligible set is a real state — a tenant whose only accounts are
  // Members — and the fix is not on this screen, so the message says where it is.
  it('explains an empty eligible set rather than showing a blank list', () => {
    TestBed.configureTestingModule({
      imports: [AdminApproversComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(AdminApproversComponent);
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    httpMock.expectOne((r) => r.url === `${API}/users`).flush(usersPage([]));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'Approver or Administrator role',
    );
  });
});
