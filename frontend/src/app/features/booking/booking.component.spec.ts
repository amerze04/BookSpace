import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { BookingComponent } from './booking.component';
import { BookingSelection } from './booking-arrival';
import { CreateBookingResponse } from './booking.models';
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
  title: { (): string; set(value: string): void };
  quantity: { (): number; set(value: number): void };
  showQuantityStepper: () => boolean;
  quantityRaisedAboveChecked: () => boolean;
  incrementQuantity(): void;
  decrementQuantity(): void;
  durationMinutes: () => number;
  durationLabel(): string;
  durationError: () => string | null;
  titleError: () => string | null;
  resourceZoneSpan: () => { date: string; timeRange: string } | null;
  viewerZoneSpan: () => { date: string; timeRange: string } | null;
  viewerTimeZoneId: string;
  canSubmit: () => boolean;
  submitting: () => boolean;
  submitFailed: () => boolean;
  created: () => CreateBookingResponse | null;
  confirmBooking(): void;
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

  describe('one-off form', () => {
    // Flushes the resource load and hands back the component, so each test
    // below starts from "loaded, with a selection".
    function loaded(
      detailOverrides: Partial<ResourceDetail> = {},
      queryParams: Record<string, string> = selectionParams,
    ): TestableBookingComponent {
      const fixture = createFixture('r1', queryParams);
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail(detailOverrides));
      return fixture.componentInstance as TestableBookingComponent;
    }

    function createdResponse(overrides: Partial<CreateBookingResponse> = {}): CreateBookingResponse {
      return {
        id: 'b1',
        resourceId: 'r1',
        userId: 'u1',
        startsAtUtc: '2026-09-24T13:15:00Z',
        endsAtUtc: '2026-09-24T15:30:00Z',
        quantity: 1,
        title: null,
        status: 'Confirmed',
        createdAtUtc: '2026-09-17T09:00:00Z',
        approval: null,
        ...overrides,
      };
    }

    describe('the request it builds', () => {
      it('posts the selected span, the quantity and a trimmed title', () => {
        const component = loaded({ capacity: 4 });
        component.title.set('  Sprint review  ');
        component.quantity.set(2);

        component.confirmBooking();

        const req = httpMock.expectOne(`${API}/bookings`);
        expect(req.request.method).toBe('POST');
        expect(req.request.body).toEqual({
          resourceId: 'r1',
          startsAtUtc: '2026-09-24T13:15:00Z',
          endsAtUtc: '2026-09-24T15:30:00Z',
          quantity: 2,
          title: 'Sprint review',
        });

        req.flush(createdResponse(), { status: 201, statusText: 'Created' });
      });

      // The column is nullable and an untitled booking is legal, so an empty
      // (or whitespace-only) box means "no title", not an empty string.
      it('sends a null title when the box is blank', () => {
        const component = loaded();
        component.title.set('   ');

        component.confirmBooking();

        const req = httpMock.expectOne(`${API}/bookings`);
        expect((req.request.body as { title: string | null }).title).toBeNull();
        req.flush(createdResponse(), { status: 201, statusText: 'Created' });
      });

      // §7's flagged gap: POST /bookings has no idempotency key, so a second
      // request would create a second booking. The in-flight guard is the
      // client's whole defence against an impatient double-click.
      it('blocks a second submit while the first is still in flight', () => {
        const component = loaded();

        component.confirmBooking();
        component.confirmBooking();

        const req = httpMock.expectOne(`${API}/bookings`);
        expect(component.submitting()).toBe(true);
        expect(component.canSubmit()).toBe(false);
        req.flush(createdResponse(), { status: 201, statusText: 'Created' });
      });

      it('does not submit again once a booking exists', () => {
        const component = loaded();
        component.confirmBooking();
        httpMock.expectOne(`${API}/bookings`).flush(createdResponse(), { status: 201, statusText: 'Created' });

        component.confirmBooking();

        httpMock.expectNone(`${API}/bookings`);
      });

      it('keeps the created booking on success, including a Pending outcome', () => {
        const component = loaded({ requiresApproval: true });

        component.confirmBooking();
        httpMock.expectOne(`${API}/bookings`).flush(
          createdResponse({ status: 'Pending', approval: { approvalRequestId: 'a1', expiresAtUtc: null } }),
          { status: 201, statusText: 'Created' },
        );

        expect(component.created()?.status).toBe('Pending');
        expect(component.submitting()).toBe(false);
        expect(component.submitFailed()).toBe(false);
      });

      it('surfaces a rejection without retrying it', () => {
        const component = loaded();

        component.confirmBooking();
        httpMock.expectOne(`${API}/bookings`).flush(
          { title: 'Conflict', status: 409, reasonCode: 'SlotUnavailable', correlationId: 'c1' },
          { status: 409, statusText: 'Conflict' },
        );

        expect(component.submitFailed()).toBe(true);
        expect(component.submitting()).toBe(false);
        expect(component.created()).toBeNull();
        // No automatic retry — the whole point of the §7 gap's mitigation.
        httpMock.expectNone(`${API}/bookings`);
      });

      it('cannot submit an archived resource', () => {
        const component = loaded({ isArchived: true });

        expect(component.canSubmit()).toBe(false);
        component.confirmBooking();

        httpMock.expectNone(`${API}/bookings`);
      });

      it('cannot submit with no selection', () => {
        const component = loaded({}, {});

        expect(component.canSubmit()).toBe(false);
        component.confirmBooking();

        httpMock.expectNone(`${API}/bookings`);
      });
    });

    describe('duration guard', () => {
      it('refuses a span shorter than the resource\'s own minimum', () => {
        // 13:15-15:30 is 135 minutes.
        const component = loaded({ minDurationMinutes: 180 });

        expect(component.durationMinutes()).toBe(135);
        expect(component.durationError()).toContain('at least 3 hours');
        expect(component.canSubmit()).toBe(false);

        component.confirmBooking();
        httpMock.expectNone(`${API}/bookings`);
      });

      it('refuses a span longer than the resource\'s own maximum', () => {
        const component = loaded({ maxDurationMinutes: 60 });

        expect(component.durationError()).toContain('at most 1 hour');
        expect(component.canSubmit()).toBe(false);
      });

      // The false-minimum regression the 2026-09-16 hardening pass fixed on
      // the availability screen: `null` means "no rule configured", never the
      // UI's own 15-minute step.
      it('invents no minimum or maximum when the resource configures neither', () => {
        const component = loaded({ minDurationMinutes: null, maxDurationMinutes: null });

        expect(component.durationError()).toBeNull();
        expect(component.canSubmit()).toBe(true);
      });

      it('accepts a span exactly on both bounds', () => {
        const component = loaded({ minDurationMinutes: 135, maxDurationMinutes: 135 });

        expect(component.durationError()).toBeNull();
      });
    });

    describe('title', () => {
      it('refuses a title past the column\'s own 200 characters', () => {
        const component = loaded();
        component.title.set('x'.repeat(201));

        expect(component.titleError()).toContain('200');
        expect(component.canSubmit()).toBe(false);

        component.confirmBooking();
        httpMock.expectNone(`${API}/bookings`);
      });

      it('accepts exactly 200', () => {
        const component = loaded();
        component.title.set('x'.repeat(200));

        expect(component.titleError()).toBeNull();
        expect(component.canSubmit()).toBe(true);
      });
    });

    describe('quantity', () => {
      // Decision 0005's amendment: Capacity 1 admits no quantity but 1, so the
      // stepper is absent rather than disabled.
      it('hides the stepper for an exclusive resource', () => {
        expect(loaded({ capacity: 1 }).showQuantityStepper()).toBe(false);
      });

      it('shows the stepper for a pooled resource', () => {
        expect(loaded({ capacity: 4 }).showQuantityStepper()).toBe(true);
      });

      it('starts from the quantity availability was checked for', () => {
        const component = loaded({ capacity: 6 }, { ...selectionParams, quantity: '3' });

        expect(component.quantity()).toBe(3);
        expect(component.quantityRaisedAboveChecked()).toBe(false);
      });

      it('clamps the stepper between 1 and the resource\'s capacity', () => {
        const component = loaded({ capacity: 2 });

        component.incrementQuantity();
        component.incrementQuantity();
        expect(component.quantity()).toBe(2);

        component.decrementQuantity();
        component.decrementQuantity();
        expect(component.quantity()).toBe(1);
      });

      // Availability answered for one quantity; asking for more is allowed
      // (only dbo.CreateBooking can really say) but flagged, so a
      // CapacityExceeded rejection isn't a surprise.
      it('flags a quantity raised above what availability was checked for', () => {
        const component = loaded({ capacity: 5 });

        component.incrementQuantity();

        expect(component.quantity()).toBe(2);
        expect(component.quantityRaisedAboveChecked()).toBe(true);
      });

      // linkedSignal, not a signal seeded once: a new selection resets it, so
      // the previous slot's number can't ride along silently.
      it('resets to the new selection\'s quantity when the query string changes', () => {
        const component = loaded({ capacity: 6 });
        component.incrementQuantity();
        expect(component.quantity()).toBe(2);

        queryParamMap$.next(
          convertToParamMap({
            startUtc: '2026-09-25T09:00:00Z',
            endUtc: '2026-09-25T10:00:00Z',
            quantity: '4',
          }),
        );

        expect(component.quantity()).toBe(4);
      });
    });

    describe('the span it reads back', () => {
      it('renders the span in the resource\'s own timezone', () => {
        const component = loaded({ timeZoneId: 'UTC' });

        expect(component.resourceZoneSpan()).toEqual({
          date: 'Thu, Sep 24, 2026',
          timeRange: '13:15 – 15:30',
        });
        expect(component.durationLabel()).toBe('2 hours 15 minutes');
      });

      it('reads the same instants in the resource\'s zone, not UTC', () => {
        const component = loaded({ timeZoneId: 'America/New_York' });

        // 13:15Z is 09:15 in New York on the same date.
        expect(component.resourceZoneSpan()).toEqual({
          date: 'Thu, Sep 24, 2026',
          timeRange: '09:15 – 11:30',
        });
      });

      // An overnight span lands on two calendar days, so the end has to carry
      // its own date rather than being read against the start's.
      it('names the end date when the span crosses local midnight', () => {
        const component = loaded(
          { timeZoneId: 'UTC' },
          { startUtc: '2026-09-24T22:00:00Z', endUtc: '2026-09-25T01:00:00Z', quantity: '1' },
        );

        expect(component.resourceZoneSpan()?.timeRange).toBe('22:00 – 01:00 (Fri, Sep 25, 2026)');
      });

      it('shows no second reading when the viewer is already in the resource\'s zone', () => {
        const component = loaded({ timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone });

        expect(component.viewerZoneSpan()).toBeNull();
      });

      it('shows the viewer\'s own reading when the zones differ', () => {
        const viewerZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        const otherZone = viewerZone === 'Pacific/Kiritimati' ? 'UTC' : 'Pacific/Kiritimati';
        const component = loaded({ timeZoneId: otherZone });

        expect(component.viewerZoneSpan()).not.toBeNull();
        expect(component.viewerZoneSpan()).not.toEqual(component.resourceZoneSpan());
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
