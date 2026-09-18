import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { ResourceDetailComponent } from '../components/resource-detail/resource-detail.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { ApproverDetail, AvailabilityWindowDetail, ResourceDetail } from '../models/resources.models';

const API = 'http://localhost:5270';

// The signals/methods this spec drives are `protected` at compile time only
// — same widened-type pattern every other component spec in this app uses.
type TestableResourceDetailComponent = ResourceDetailComponent & {
  resource: () => ResourceDetail | null;
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  weekdayRows: () => { weekday: string; hours: string | null }[];
  retry(): void;
  capacityLabel(resource: ResourceDetail): string;
  minDurationLabel(resource: ResourceDetail): string;
  maxDurationLabel(resource: ResourceDetail): string;
  approverNames(resource: ResourceDetail): string;
};

function fakeWindow(overrides: Partial<AvailabilityWindowDetail> = {}): AvailabilityWindowDetail {
  return { id: 'w1', weekday: 'Monday', opensAt: '08:00:00', closesAt: '18:00:00', ...overrides };
}

function fakeApprover(overrides: Partial<ApproverDetail> = {}): ApproverDetail {
  return { userId: 'u1', fullName: 'Facilities Team', ...overrides };
}

function fakeDetail(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: 'Main conference room',
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'Europe/Sarajevo',
    requiresApproval: false,
    minDurationMinutes: 30,
    maxDurationMinutes: 240,
    isArchived: false,
    createdAtUtc: '2026-08-21T00:00:00Z',
    updatedAtUtc: '2026-08-21T00:00:00Z',
    availabilityWindows: [],
    approvers: [],
    ...overrides,
  };
}

describe('ResourceDetailComponent', () => {
  let httpMock: HttpTestingController;
  let breadcrumbService: BreadcrumbService;
  let paramMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  // A BehaviorSubject, not a bare `of(...)`, so a test can push a new id
  // through it and prove the component reacts — the same "don't trust a
  // one-time snapshot" case the component's own comment explains.
  function createFixture(id = 'r1') {
    paramMap$ = new BehaviorSubject(convertToParamMap({ id }));

    TestBed.configureTestingModule({
      imports: [ResourceDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // A real Router rather than a `{ navigate }` stub: RouterLink builds
        // its href through createUrlTree/serializeUrl, so the recurring entry
        // point's own query params can only be asserted against a real one.
        // The ActivatedRoute stub below still wins for the route params, being
        // provided last.
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: convertToParamMap({ id }) },
            paramMap: paramMap$,
          },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    breadcrumbService = TestBed.inject(BreadcrumbService);
    return TestBed.createComponent(ResourceDetailComponent);
  }

  afterEach(() => {
    httpMock.verify();
    breadcrumbService.setOverride(null);
  });

  it('fetches the resource named by the route id', () => {
    createFixture('r1');

    const req = httpMock.expectOne(`${API}/resources/r1`);
    expect(req.request.method).toBe('GET');
    req.flush(fakeDetail());
  });

  it('sets the resource on success and sets the breadcrumb override to its name', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ name: 'Conference Room A' }));

    expect(component.resource()?.name).toBe('Conference Room A');
    expect(component.loading()).toBe(false);
    expect(breadcrumbService.override()).toBe('Conference Room A');
  });

  it('clears the breadcrumb override once the component is destroyed', () => {
    const fixture = createFixture();
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ name: 'Conference Room A' }));
    expect(breadcrumbService.override()).toBe('Conference Room A');

    fixture.destroy();

    expect(breadcrumbService.override()).toBeNull();
  });

  it('shows a not-found state on a 404, distinct from a generic load error', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
    expect(component.loadError()).toBe(false);
  });

  it('shows a generic error and recovers via retry() on anything else', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);
    expect(component.notFound()).toBe(false);

    component.retry();
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

    expect(component.loadError()).toBe(false);
    expect(component.resource()).not.toBeNull();
  });

  it('re-fetches when the route param changes without the component being recreated', () => {
    const fixture = createFixture('r1');
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ id: 'r1', name: 'Conference Room A' }));

    paramMap$.next(convertToParamMap({ id: 'r2' }));

    const req = httpMock.expectOne(`${API}/resources/r2`);
    req.flush(fakeDetail({ id: 'r2', name: 'Pool Cars' }));

    const component = fixture.componentInstance as TestableResourceDetailComponent;
    expect(component.resource()?.name).toBe('Pool Cars');
  });

  // Item 4: A starts loading -> route changes to B -> B completes -> A
  // completes later — A must never win. switchMap makes this true by
  // construction (it cancels A's in-flight request the moment B's id comes
  // through), not by a manual "is this response still current" check.
  it('cancels a still-pending fetch when the route id changes before it resolves, so a stale response can never overwrite the newer one', () => {
    const fixture = createFixture('r1');
    const component = fixture.componentInstance as TestableResourceDetailComponent;
    const firstReq = httpMock.expectOne(`${API}/resources/r1`);

    paramMap$.next(convertToParamMap({ id: 'r2' }));

    const secondReq = httpMock.expectOne(`${API}/resources/r2`);
    secondReq.flush(fakeDetail({ id: 'r2', name: 'Pool Cars' }));

    expect(firstReq.cancelled).toBe(true);
    expect(component.resource()?.name).toBe('Pool Cars');
    expect(component.loading()).toBe(false);
  });

  it('builds all seven weekday rows, Monday first, "Not bookable" where there is no window', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(
      fakeDetail({
        availabilityWindows: [fakeWindow({ weekday: 'Monday' }), fakeWindow({ weekday: 'Friday', opensAt: '09:00:00', closesAt: '13:00:00' })],
      }),
    );

    const rows = component.weekdayRows();
    expect(rows.map((r) => r.weekday)).toEqual([
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ]);
    expect(rows.find((r) => r.weekday === 'Monday')?.hours).toBe('08:00 – 18:00');
    expect(rows.find((r) => r.weekday === 'Friday')?.hours).toBe('09:00 – 13:00');
    expect(rows.find((r) => r.weekday === 'Tuesday')?.hours).toBeNull();
  });

  it('joins multiple windows on the same weekday with a comma', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(
      fakeDetail({
        availabilityWindows: [
          fakeWindow({ weekday: 'Monday', opensAt: '08:00:00', closesAt: '12:00:00' }),
          fakeWindow({ weekday: 'Monday', opensAt: '13:00:00', closesAt: '18:00:00' }),
        ],
      }),
    );

    expect(component.weekdayRows().find((r) => r.weekday === 'Monday')?.hours).toBe('08:00 – 12:00, 13:00 – 18:00');
  });

  it('formats durations in minutes as hours/minutes, and null as the given fallback', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

    expect(component.minDurationLabel(fakeDetail({ minDurationMinutes: 30 }))).toBe('30 min');
    expect(component.minDurationLabel(fakeDetail({ minDurationMinutes: null }))).toBe('No minimum');
    expect(component.maxDurationLabel(fakeDetail({ maxDurationMinutes: 240 }))).toBe('4 hours');
    expect(component.maxDurationLabel(fakeDetail({ maxDurationMinutes: 90 }))).toBe('1 hour 30 min');
    expect(component.maxDurationLabel(fakeDetail({ maxDurationMinutes: 60 }))).toBe('1 hour');
    expect(component.maxDurationLabel(fakeDetail({ maxDurationMinutes: null }))).toBe('No maximum');
  });

  it('lists approver full names, or a fallback when none are assigned', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

    expect(component.approverNames(fakeDetail({ approvers: [fakeApprover(), fakeApprover({ userId: 'u2', fullName: 'Jane Doe' })] }))).toBe(
      'Facilities Team, Jane Doe',
    );
    expect(component.approverNames(fakeDetail({ approvers: [] }))).toBe('None assigned');
  });

  it('labels an exclusive resource "Single resource" and a pooled one by its unit count', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableResourceDetailComponent;
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

    expect(component.capacityLabel(fakeDetail({ capacity: 1 }))).toBe('Single resource');
    expect(component.capacityLabel(fakeDetail({ capacity: 5 }))).toBe('5 units');
  });

  // Item 9: "Check availability" leads somewhere that correctly refuses to
  // book an archived resource anyway, but offering it at all is a dead end
  // worth not presenting in the first place.
  it('shows a plain archived notice instead of "Check availability" for an archived resource', () => {
    const fixture = createFixture();
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ isArchived: true }));
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.primary-button')).toBeNull();
    expect(el.querySelector('.archived-notice')?.textContent).toContain('Archived');
  });

  it('shows "Check availability" for an active resource', () => {
    const fixture = createFixture();
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ isArchived: false }));
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.primary-button')?.textContent).toContain('Check availability');
    expect(el.querySelector('.archived-notice')).toBeNull();
  });

  // The recurring entry point (owner's call, 2026-09-17): a series names its
  // own schedule, so requiring a slot to be picked first made the availability
  // screen a toll booth rather than a step. Picking one still works and still
  // pre-fills the form — it is simply no longer the only way in.
  it('offers a direct route to a recurring booking, carrying the mode', () => {
    const fixture = createFixture();
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ isArchived: false }));
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector('.secondary-button');
    expect(link?.textContent).toContain('Book a recurring series');
    expect(link?.getAttribute('href')).toBe('/resources/r1/book?mode=recurring');
  });

  it('offers no recurring route for an archived resource either', () => {
    const fixture = createFixture();
    httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ isArchived: true }));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.secondary-button')).toBeNull();
  });
});
