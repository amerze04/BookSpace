import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { BookingComponent } from './booking.component';
import { BookingSelection } from './booking-arrival';
import { BreadcrumbService } from '../../layout/breadcrumb.service';
import { ResourceDetail } from '../resources/resources.models';

const API = 'http://localhost:5270';

// Same widened-type pattern every other component spec in this app uses: the
// members below are `protected` at compile time only.
type TestableBookingComponent = BookingComponent & {
  resource: () => ResourceDetail | null;
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  isArchived: () => boolean;
  selection: () => BookingSelection | null;
  retry(): void;
  typeLabel(type: ResourceDetail['resourceType']): string;
  capacityLabel(resource: ResourceDetail): string;
  approvalLabel(resource: ResourceDetail): string;
};

function fakeDetail(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: 'Main conference room',
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'America/New_York',
    requiresApproval: true,
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

// The query string the availability screen navigates with.
const selectionParams = {
  startUtc: '2026-09-24T13:15:00Z',
  endUtc: '2026-09-24T15:30:00Z',
  quantity: '1',
};

describe('BookingComponent', () => {
  let httpMock: HttpTestingController;
  let breadcrumbService: BreadcrumbService;
  let paramMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let queryParamMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  // Both maps are BehaviorSubjects rather than bare `of(...)`: the router can
  // reuse this component across a change to either the `:id` or the selected
  // slot, and both paths are asserted below.
  function createFixture(id = 'r1', queryParams: Record<string, string> = selectionParams) {
    paramMap$ = new BehaviorSubject(convertToParamMap({ id }));
    queryParamMap$ = new BehaviorSubject(convertToParamMap(queryParams));

    TestBed.configureTestingModule({
      imports: [BookingComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: convertToParamMap({ id }) },
            paramMap: paramMap$,
            queryParamMap: queryParamMap$,
          },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    breadcrumbService = TestBed.inject(BreadcrumbService);
    return TestBed.createComponent(BookingComponent);
  }

  afterEach(() => {
    httpMock.verify();
    breadcrumbService.setInsertBeforeLast(null);
  });

  describe('resource load', () => {
    it('fetches the resource named by the route id', () => {
      createFixture('r1');

      const req = httpMock.expectOne(`${API}/resources/r1`);
      expect(req.request.method).toBe('GET');
      req.flush(fakeDetail());
    });

    it('sets the resource on success and inserts its name into the breadcrumb', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      expect(component.loading()).toBe(false);
      expect(component.resource()?.name).toBe('Conference Room A');
      // insertBeforeLast, not override: this route is a sibling of `:id`, so
      // the resource has no crumb of its own to replace.
      expect(breadcrumbService.insertBeforeLast()).toBe('Conference Room A');
      expect(breadcrumbService.override()).toBeNull();
    });

    it('shows the not-found state for a 404, distinct from a generic failure', () => {
      const fixture = createFixture('missing');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/missing`).flush(
        { title: 'Not found', status: 404, reasonCode: 'ResourceNotFound', correlationId: 'c1' },
        { status: 404, statusText: 'Not Found' },
      );

      expect(component.notFound()).toBe(true);
      expect(component.loadError()).toBe(false);
    });

    it('shows a retryable error for any other failure, and retry() re-fetches', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush('boom', { status: 500, statusText: 'Server Error' });
      expect(component.loadError()).toBe(true);
      expect(component.notFound()).toBe(false);

      component.retry();
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());
      expect(component.loadError()).toBe(false);
      expect(component.resource()?.id).toBe('r1');
    });

    it('re-fetches when the route id changes without the component being recreated', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      paramMap$.next(convertToParamMap({ id: 'r2' }));
      httpMock.expectOne(`${API}/resources/r2`).flush(fakeDetail({ id: 'r2', name: 'Pool Cars' }));

      expect(component.resource()?.name).toBe('Pool Cars');
      expect(breadcrumbService.insertBeforeLast()).toBe('Pool Cars');
    });

    it('clears its breadcrumb entry when destroyed', () => {
      const fixture = createFixture('r1');
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());
      expect(breadcrumbService.insertBeforeLast()).toBe('Conference Room A');

      fixture.destroy();

      expect(breadcrumbService.insertBeforeLast()).toBeNull();
    });
  });

  describe('selected slot', () => {
    it('reads the selection out of the query string', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      expect(component.selection()).toEqual({
        startUtc: '2026-09-24T13:15:00Z',
        endUtc: '2026-09-24T15:30:00Z',
        quantity: 1,
      });
    });

    it('carries a pooled quantity through unchanged', () => {
      const fixture = createFixture('r1', { ...selectionParams, quantity: '3' });
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ capacity: 5 }));

      expect(component.selection()?.quantity).toBe(3);
    });

    // A bare deep link — the URL with no selection on it. Not an error: this
    // is the "pick a time first" panel's own state.
    it('has no selection when the query string carries none', () => {
      const fixture = createFixture('r1', {});
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      expect(component.selection()).toBeNull();
    });

    it('treats a malformed query string as no selection at all', () => {
      const fixture = createFixture('r1', { ...selectionParams, quantity: '0' });
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      expect(component.selection()).toBeNull();
    });

    // The query string is observed, not read once: picking a second slot on
    // the same resource changes only the query params, and the router reuses
    // this component instance rather than recreating it.
    it('follows a query-string change without the component being recreated', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      queryParamMap$.next(
        convertToParamMap({
          startUtc: '2026-09-25T09:00:00Z',
          endUtc: '2026-09-25T10:00:00Z',
          quantity: '2',
        }),
      );

      expect(component.selection()).toEqual({
        startUtc: '2026-09-25T09:00:00Z',
        endUtc: '2026-09-25T10:00:00Z',
        quantity: 2,
      });
    });
  });

  describe('resource state and labels', () => {
    it('flags an archived resource, which is not bookable at all', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ isArchived: true }));

      expect(component.isArchived()).toBe(true);
    });

    it('is not archived while the resource has not loaded', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      expect(component.isArchived()).toBe(false);

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());
    });

    it('labels the resource the same way every other resource screen does', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;

      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      expect(component.typeLabel('LabSlot')).toBe('Lab slot');
      expect(component.capacityLabel(fakeDetail({ capacity: 1 }))).toBe('Single resource');
      expect(component.capacityLabel(fakeDetail({ capacity: 4 }))).toBe('4 units');
      expect(component.approvalLabel(fakeDetail({ requiresApproval: true }))).toBe('Approval required');
      expect(component.approvalLabel(fakeDetail({ requiresApproval: false }))).toBe('Instant confirmation');
    });
  });
});
