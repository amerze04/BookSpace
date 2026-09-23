import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { AdminAvailabilityWindowsComponent } from '../components/admin-availability-windows/admin-availability-windows.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { AvailabilityWindowDetail, ResourceDetail } from '../../resources/models/resources.models';
import { WindowRow } from '../windows/window-editor';

const API = 'http://localhost:5270';

type TestableEditor = AdminAvailabilityWindowsComponent & {
  rows: () => WindowRow[];
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  saving: () => boolean;
  saved: () => boolean;
  dirty: () => boolean;
  canSave: () => boolean;
  isArchived: () => boolean;
  timeZoneId: () => string;
  errors: () => Array<{ key: string; message: string }>;
  rejection: () => { formMessage: string | null } | null;
  addRow(weekday: string): void;
  removeRow(key: string): void;
  onOpensAtInput(key: string, event: Event): void;
  onClosesAtInput(key: string, event: Event): void;
  onEndOfDayChange(key: string, event: Event): void;
  save(): void;
  retryLoad(): void;
};

function input(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function checkbox(checked: boolean): Event {
  return { target: { checked } } as unknown as Event;
}

function problem(status: number, reasonCode: string) {
  return { status, title: 'The request was refused.', reasonCode };
}

function window_(overrides: Partial<AvailabilityWindowDetail> = {}): AvailabilityWindowDetail {
  return { id: 'w1', weekday: 'Monday', opensAt: '09:00:00', closesAt: '17:00:00', ...overrides };
}

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
    availabilityWindows: [window_()],
    approvers: [],
    ...overrides,
  };
}

describe('AdminAvailabilityWindowsComponent', () => {
  let httpMock: HttpTestingController;

  function create(): TestableEditor {
    TestBed.configureTestingModule({
      imports: [AdminAvailabilityWindowsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: () => 'r1' } } },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminAvailabilityWindowsComponent)
      .componentInstance as TestableEditor;
  }

  function createLoaded(resource: ResourceDetail = detail()): TestableEditor {
    const component = create();
    httpMock.expectOne(`${API}/resources/r1`).flush(resource);
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

  it('loads the resource and seeds a row per stored window', () => {
    const component = createLoaded(
      detail({
        availabilityWindows: [
          window_({ id: 'a', weekday: 'Monday' }),
          window_({ id: 'b', weekday: 'Wednesday', opensAt: '08:00:00', closesAt: '12:00:00' }),
        ],
      }),
    );

    expect(component.rows()).toHaveLength(2);
    expect(component.loading()).toBe(false);
  });

  // Decision `0003`, and the most misreadable thing on the screen: the times
  // are the resource's wall clock, not the administrator's.
  it('names the resource timezone the hours are written in', () => {
    const component = createLoaded(detail({ timeZoneId: 'America/New_York' }));

    expect(component.timeZoneId()).toBe('America/New_York');
  });

  // The route chain already ends in "Availability", so the resource name is
  // *inserted* before it rather than replacing it — the same shape the
  // member-facing availability screen uses.
  it('inserts the resource name into the breadcrumb without replacing the last crumb', () => {
    createLoaded();

    const breadcrumbs = TestBed.inject(BreadcrumbService);
    expect(breadcrumbs.insertBeforeLast()).toBe('Conference Room A');
    expect(breadcrumbs.override()).toBeNull();
  });

  it('shows a not-found state for a resource that is not this tenant’s', () => {
    const component = create();
    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(404, 'ResourceNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
  });

  it('shows a retryable error for a load failure that is not a 404', () => {
    const component = create();
    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);

    component.retryLoad();
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    expect(component.loadError()).toBe(false);
  });

  // ---- Dirty tracking ----

  // The endpoint replaces the whole set, so re-sending an identical schedule is
  // harmless but pointless — and a save button that always looks available
  // hides whether the last save actually landed.
  it('will not save until something has changed', () => {
    const component = createLoaded();

    expect(component.dirty()).toBe(false);
    expect(component.canSave()).toBe(false);

    component.save();
    httpMock.expectNone(`${API}/resources/r1/availability-windows`);
  });

  it('becomes saveable once a window moves', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('18:00'));

    expect(component.dirty()).toBe(true);
    expect(component.canSave()).toBe(true);
  });

  it('becomes saveable when a day is cleared entirely', () => {
    const component = createLoaded();
    component.removeRow(component.rows()[0].key);

    expect(component.rows()).toEqual([]);
    expect(component.canSave()).toBe(true);
  });

  // ---- Client-side rules ----

  // `docs/admin-plan.md` §4 asks for overlaps to be refused before the server
  // has to. Not because the server would miss it — it answers 409 — but because
  // a whole weekly schedule refused as one request tells an admin nothing about
  // which of fourteen rows was wrong.
  it('refuses to submit an overlapping schedule, and says which row', () => {
    const component = createLoaded();
    component.addRow('Monday');
    const added = component.rows()[component.rows().length - 1];
    component.onOpensAtInput(added.key, input('10:00'));

    expect(component.errors()).toHaveLength(1);
    expect(component.errors()[0].message).toContain('overlaps');
    expect(component.canSave()).toBe(false);

    component.save();
    httpMock.expectNone(`${API}/resources/r1/availability-windows`);
  });

  it('refuses a window that closes before it opens', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('08:00'));

    expect(component.errors()[0].message).toContain('after the opening time');
    expect(component.canSave()).toBe(false);
  });

  // ---- Saving ----

  it('sends the whole set, with wire-shaped times', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('18:30'));

    component.save();

    const request = httpMock.expectOne(`${API}/resources/r1/availability-windows`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      windows: [{ weekday: 'Monday', opensAt: '09:00:00', closesAt: '18:30:00' }],
    });

    request.flush({
      resourceId: 'r1',
      availabilityWindows: [window_({ closesAt: '18:30:00' })],
    });

    expect(component.saved()).toBe(true);
  });

  // **Decision `0022` on the wire.** The tick means the following midnight, and
  // 23:59:59 is the only way a `time` column can say it.
  it('writes an end-of-day window as 23:59:59', () => {
    const component = createLoaded();
    component.onEndOfDayChange(component.rows()[0].key, checkbox(true));

    component.save();

    const request = httpMock.expectOne(`${API}/resources/r1/availability-windows`);
    expect(request.request.body.windows[0].closesAt).toBe('23:59:59');
    request.flush({ resourceId: 'r1', availabilityWindows: [window_({ closesAt: '23:59:59' })] });
  });

  // Clearing a schedule is a real request, not something achieved by omission —
  // the backend validator says the same about an empty array.
  it('sends an empty array to clear a schedule', () => {
    const component = createLoaded();
    component.removeRow(component.rows()[0].key);

    component.save();

    const request = httpMock.expectOne(`${API}/resources/r1/availability-windows`);
    expect(request.request.body).toEqual({ windows: [] });
    request.flush({ resourceId: 'r1', availabilityWindows: [] });
  });

  // Re-seeded from the response, never from what was typed: the server assigned
  // ids and normalized the order, and this is what makes the form fall back to
  // clean without a second read.
  it('re-seeds from the response and stops being dirty', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('18:00'));
    component.save();

    httpMock
      .expectOne(`${API}/resources/r1/availability-windows`)
      .flush({ resourceId: 'r1', availabilityWindows: [window_({ id: 'server-id', closesAt: '18:00:00' })] });

    expect(component.dirty()).toBe(false);
    expect(component.canSave()).toBe(false);
    expect(component.rows()[0].closesAt).toBe('18:00');
  });

  // ---- Refusals ----

  it('explains an overlap the server caught, in terms of what it compared', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('18:00'));
    component.save();

    httpMock
      .expectOne(`${API}/resources/r1/availability-windows`)
      .flush(problem(409, 'OverlappingAvailabilityWindow'), { status: 409, statusText: 'Conflict' });

    expect(component.rejection()?.formMessage).toContain('overlap');
    expect(component.saving()).toBe(false);
  });

  it('says an archived resource can no longer have its hours changed', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('18:00'));
    component.save();

    httpMock
      .expectOne(`${API}/resources/r1/availability-windows`)
      .flush(problem(422, 'ResourceArchived'), { status: 422, statusText: 'Unprocessable' });

    expect(component.rejection()?.formMessage).toContain('archived');
  });

  // A rejection must not sit above a form that has since changed.
  it('clears a rejection as soon as the schedule is edited again', () => {
    const component = createLoaded();
    component.onClosesAtInput(component.rows()[0].key, input('18:00'));
    component.save();
    httpMock
      .expectOne(`${API}/resources/r1/availability-windows`)
      .flush(problem(409, 'ConcurrencyConflict'), { status: 409, statusText: 'Conflict' });
    expect(component.rejection()).not.toBeNull();

    component.onClosesAtInput(component.rows()[0].key, input('19:00'));

    expect(component.rejection()).toBeNull();
  });

  // ---- Archived ----

  // FR-3.5: an archived resource accepts no edits, so the editor must not offer
  // a save that answers 422.
  it('is read-only for an archived resource', () => {
    const component = createLoaded(detail({ isArchived: true }));

    expect(component.isArchived()).toBe(true);

    component.removeRow(component.rows()[0].key);
    expect(component.canSave()).toBe(false);

    component.save();
    httpMock.expectNone(`${API}/resources/r1/availability-windows`);
  });

  // ---- Rendered output ----

  it('renders every weekday, and says Closed for a day with no windows', () => {
    TestBed.configureTestingModule({
      imports: [AdminAvailabilityWindowsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'r1' } } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(AdminAvailabilityWindowsComponent);
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    fixture.detectChanges();

    const dom = fixture.nativeElement as HTMLElement;
    const text = dom.textContent ?? '';

    for (const day of ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']) {
      expect(text).toContain(day);
    }

    // Only Monday has a window in the fixture, so the other six read Closed.
    expect(dom.querySelectorAll('.day-closed')).toHaveLength(6);

    // And the timezone is on screen, not merely in a signal.
    expect(text).toContain('Europe/Warsaw');
  });
});
