import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { AdminBlackoutsComponent } from '../components/admin-blackouts/admin-blackouts.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { ResourceDetail } from '../../resources/models/resources.models';
import {
  BlackoutPeriodSummary,
  CancelledBooking,
} from '../../availability/models/blackout-periods.models';
import { PagedResult } from '../../../core/http/paged-result';

const API = 'http://localhost:5270';

type TestableBlackouts = AdminBlackoutsComponent & {
  mode: () => 'list' | 'create' | 'edit';
  blackouts: () => BlackoutPeriodSummary[];
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  submitting: () => boolean;
  isArchived: () => boolean;
  canSubmit: () => boolean;
  startsAt: () => string;
  endsAt: () => string;
  reason: () => string;
  cancelled: () => CancelledBooking[] | null;
  preview: () => Array<{ id: string }> | null;
  previewFailed: () => boolean;
  deletingId: () => string | null;
  rejection: () => { formMessage: string | null; fieldMessages: Record<string, string> } | null;
  endsAtError: () => string | null;
  startCreating(): void;
  startEditing(blackout: BlackoutPeriodSummary): void;
  cancelForm(): void;
  onStartsAtInput(event: Event): void;
  onEndsAtInput(event: Event): void;
  onReasonInput(event: Event): void;
  checkImpact(): void;
  submit(): void;
  startDeleting(id: string): void;
  cancelDeleting(): void;
  confirmDelete(): void;
};

function input(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function problem(status: number, reasonCode: string) {
  return { status, title: 'The request was refused.', reasonCode };
}

// Warsaw throughout, so the UTC instants below are the summer offset (+2) and a
// conversion that silently did nothing would show up immediately.
function detail(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: null,
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'Europe/Warsaw',
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

function blackout(overrides: Partial<BlackoutPeriodSummary> = {}): BlackoutPeriodSummary {
  return {
    id: 'b1',
    resourceId: 'r1',
    startsAtUtc: '2026-07-15T07:00:00Z',
    endsAtUtc: '2026-07-15T15:00:00Z',
    reason: 'Deep clean',
    createdAtUtc: '2026-07-01T09:00:00Z',
    ...overrides,
  };
}

function page<T>(items: T[]): PagedResult<T> {
  return {
    items,
    page: 1,
    pageSize: 100,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
  };
}

function booking(overrides: Record<string, unknown> = {}) {
  return {
    id: 'bk1',
    resourceId: 'r1',
    resourceName: 'Conference Room A',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: '2030-07-15T08:00:00Z',
    endsAtUtc: '2030-07-15T09:00:00Z',
    quantity: 1,
    title: null,
    status: 'Confirmed',
    createdAtUtc: '2026-07-01T09:00:00Z',
    ...overrides,
  };
}

describe('AdminBlackoutsComponent', () => {
  let httpMock: HttpTestingController;

  function create(): TestableBlackouts {
    TestBed.configureTestingModule({
      imports: [AdminBlackoutsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminBlackoutsComponent).componentInstance as TestableBlackouts;
  }

  // `forkJoin` cancels its sibling the instant one errors, so a failing read has
  // to be flushed *second* — the lesson phase 5's spec recorded.
  function createLoaded(
    resource: ResourceDetail = detail(),
    rows: BlackoutPeriodSummary[] = [blackout()],
  ): TestableBlackouts {
    const component = create();
    httpMock.expectOne(`${API}/resources/r1`).flush(resource);
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page(rows));
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

  it('loads the resource and its blackouts together', () => {
    const component = createLoaded();

    expect(component.loading()).toBe(false);
    expect(component.blackouts()).toHaveLength(1);
    expect(component.mode()).toBe('list');
  });

  it('inserts the resource name into the breadcrumb without replacing the last crumb', () => {
    createLoaded();

    expect(TestBed.inject(BreadcrumbService).insertBeforeLast()).toBe('Conference Room A');
  });

  it('shows a not-found state for a resource that is not this tenant’s', () => {
    const component = create();
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([]));
    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(404, 'ResourceNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
  });

  // ---- Editing seeds from the stored instant, in the resource's zone ----

  // The blackout is stored as 07:00Z; Warsaw in July is +2, so the form must
  // show 09:00. A conversion that did nothing would show 07:00.
  it('seeds the form in the resource timezone, not in UTC', () => {
    const component = createLoaded();

    component.startEditing(blackout());

    expect(component.startsAt()).toBe('2026-07-15T09:00');
    expect(component.endsAt()).toBe('2026-07-15T17:00');
    expect(component.reason()).toBe('Deep clean');
  });

  // ---- The preview (§4.4) ----

  // **The query has to match the cascade's own predicate**: same resource, the
  // same overlap window, and tenant scope — the cascade does not care whose
  // bookings they are.
  it('asks for the bookings the cascade would take, in tenant scope', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));

    component.checkImpact();

    const request = httpMock.expectOne((r) => r.url === `${API}/bookings`);
    expect(request.request.params.get('resourceId')).toBe('r1');
    expect(request.request.params.get('scope')).toBe('tenant');
    expect(request.request.params.get('from')).toBe('2030-07-15T07:00:00Z');
    expect(request.request.params.get('to')).toBe('2030-07-15T15:00:00Z');

    request.flush(page([booking()]));

    expect(component.preview()).toHaveLength(1);
  });

  // The cascade takes Pending and Confirmed only, and only what has not already
  // ended. Filtered client-side because `status` takes one value, not a set.
  it('previews only what the cascade would actually cancel', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));
    component.checkImpact();

    httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(
      page([
        booking({ id: 'keep-confirmed', status: 'Confirmed' }),
        booking({ id: 'keep-pending', status: 'Pending' }),
        booking({ id: 'drop-cancelled', status: 'Cancelled' }),
        booking({ id: 'drop-rejected', status: 'Rejected' }),
        booking({ id: 'drop-past', status: 'Confirmed', endsAtUtc: '2020-01-01T09:00:00Z' }),
      ]),
    );

    expect(component.preview()?.map((b) => b.id)).toEqual(['keep-confirmed', 'keep-pending']);
  });

  // A forecast made for one window is not a forecast for another.
  it('drops the preview as soon as the window changes', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));
    component.checkImpact();
    httpMock.expectOne((r) => r.url === `${API}/bookings`).flush(page([booking()]));
    expect(component.preview()).not.toBeNull();

    component.onEndsAtInput(input('2030-07-15T18:00'));

    expect(component.preview()).toBeNull();
  });

  // The preview is a convenience, not a gate — a failed forecast must not stop
  // an admin saving, because the response reports the truth either way.
  it('still allows saving when the preview fails', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));
    component.checkImpact();

    httpMock
      .expectOne((r) => r.url === `${API}/bookings`)
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.previewFailed()).toBe(true);
    expect(component.canSubmit()).toBe(true);
  });

  // ---- Creating ----

  it('sends UTC instants converted from the resource timezone', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));
    component.onReasonInput(input('  Rewiring  ') as unknown as Event);

    component.submit();

    const request = httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods` && r.method === 'POST');
    expect(request.request.body).toEqual({
      startsAtUtc: '2030-07-15T07:00:00Z',
      endsAtUtc: '2030-07-15T15:00:00Z',
      reason: 'Rewiring',
    });

    request.flush({
      id: 'b2',
      resourceId: 'r1',
      startsAtUtc: '2030-07-15T07:00:00Z',
      endsAtUtc: '2030-07-15T15:00:00Z',
      reason: 'Rewiring',
      createdAtUtc: '2026-09-23T09:00:00Z',
      cancelledBookings: [],
    });
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([blackout()]));

    expect(component.mode()).toBe('list');
    expect(component.cancelled()).toEqual([]);
  });

  // An empty reason is null, not an empty string — the column is nullable and
  // "no reason given" is a real answer.
  it('sends a blank reason as null', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));

    component.submit();

    const request = httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods` && r.method === 'POST');
    expect(request.request.body.reason).toBeNull();
    request.flush({
      id: 'b2',
      resourceId: 'r1',
      startsAtUtc: '2030-07-15T07:00:00Z',
      endsAtUtc: '2030-07-15T15:00:00Z',
      reason: null,
      createdAtUtc: '2026-09-23T09:00:00Z',
      cancelledBookings: [],
    });
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([]));
  });

  // **§4.4's real answer.** The record comes from the response, not the preview.
  it('reports what was actually cancelled, from the response', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));

    component.submit();

    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods` && r.method === 'POST').flush({
      id: 'b2',
      resourceId: 'r1',
      startsAtUtc: '2030-07-15T07:00:00Z',
      endsAtUtc: '2030-07-15T15:00:00Z',
      reason: null,
      createdAtUtc: '2026-09-23T09:00:00Z',
      cancelledBookings: [
        {
          bookingId: 'bk1',
          userId: 'u1',
          startsAtUtc: '2030-07-15T08:00:00Z',
          endsAtUtc: '2030-07-15T09:00:00Z',
          recurrenceRuleId: 'rr1',
        },
      ],
    });
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([]));

    expect(component.cancelled()).toHaveLength(1);
    expect(component.cancelled()![0].recurrenceRuleId).toBe('rr1');
  });

  it('refuses to submit an interval whose end is not after its start', () => {
    const component = createLoaded();
    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T17:00'));
    component.onEndsAtInput(input('2030-07-15T09:00'));

    expect(component.endsAtError()).toContain('after the start');
    expect(component.canSubmit()).toBe(false);

    component.submit();
    httpMock.expectNone((r) => r.method === 'POST');
  });

  // ---- Editing ----

  it('edits through PUT against the blackout id', () => {
    const component = createLoaded();
    component.startEditing(blackout());
    component.onEndsAtInput(input('2026-07-15T18:00'));

    component.submit();

    const request = httpMock.expectOne(
      (r) => r.url === `${API}/resources/r1/blackout-periods/b1` && r.method === 'PUT',
    );
    expect(request.request.body.endsAtUtc).toBe('2026-07-15T16:00:00Z');

    request.flush({
      id: 'b1',
      resourceId: 'r1',
      startsAtUtc: '2026-07-15T07:00:00Z',
      endsAtUtc: '2026-07-15T16:00:00Z',
      reason: 'Deep clean',
      createdAtUtc: '2026-07-01T09:00:00Z',
      updatedAtUtc: '2026-09-23T09:00:00Z',
      cancelledBookings: [],
    });
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([blackout()]));

    expect(component.mode()).toBe('list');
  });

  // ---- Deleting ----

  it('confirms before deleting, and deletes by id', () => {
    const component = createLoaded();

    component.startDeleting('b1');
    expect(component.deletingId()).toBe('b1');

    component.confirmDelete();

    const request = httpMock.expectOne(
      (r) => r.url === `${API}/resources/r1/blackout-periods/b1` && r.method === 'DELETE',
    );
    request.flush(null);
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([]));

    expect(component.deletingId()).toBeNull();
    expect(component.blackouts()).toEqual([]);
  });

  it('deletes nothing until the confirmation is opened', () => {
    const component = createLoaded();

    component.confirmDelete();

    httpMock.expectNone((r) => r.method === 'DELETE');
  });

  it('explains a blackout that was already deleted', () => {
    const component = createLoaded();
    component.startDeleting('b1');
    component.confirmDelete();

    httpMock
      .expectOne((r) => r.method === 'DELETE')
      .flush(problem(404, 'BlackoutPeriodNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.rejection()?.formMessage).toContain('no longer exists');
  });

  // ---- Archived ----

  it('is read-only for an archived resource', () => {
    const component = createLoaded(detail({ isArchived: true }));

    expect(component.isArchived()).toBe(true);

    component.startCreating();
    component.onStartsAtInput(input('2030-07-15T09:00'));
    component.onEndsAtInput(input('2030-07-15T17:00'));

    expect(component.canSubmit()).toBe(false);
    component.submit();
    httpMock.expectNone((r) => r.method === 'POST');
  });

  // ---- Rendered output ----

  it('says plainly that bookings overlapping a blackout are cancelled', () => {
    TestBed.configureTestingModule({
      imports: [AdminBlackoutsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(AdminBlackoutsComponent);
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([blackout()]));
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('are cancelled');
    expect(text).toContain('Europe/Warsaw');
  });

  // **The delete confirmation's load-bearing sentence.** Decision `0019`'s
  // cascade is forwards-only, so deleting a blackout is not an undo. An admin
  // who thought it was would be wrong in a way nothing else corrects.
  it('says deleting does not restore the bookings it cancelled', () => {
    TestBed.configureTestingModule({
      imports: [AdminBlackoutsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(AdminBlackoutsComponent);
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`).flush(page([blackout()]));

    (fixture.componentInstance as TestableBlackouts).startDeleting('b1');
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('stay cancelled');
    expect(text).toContain('does not restore');
    expect(text).toContain('cannot be undone');
  });
});
