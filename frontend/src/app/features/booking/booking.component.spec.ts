import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { BookingComponent } from './booking.component';
import { BookingSelection } from './booking-arrival';
import { CreateBookingResponse } from './booking.models';
import { BookingRejection } from './booking-rejection';
import { RecurrenceFormErrors, RecurrenceFormValue } from './recurrence-form';
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
  rejection: () => BookingRejection | null;
  quantityError: () => string | null;
  capacityError: () => string | null;
  topOfFormMessage: () => string | null;
  topOfFormRecheck: () => boolean;
  created: () => CreateBookingResponse | null;
  confirmBooking(): void;
  isPending: () => boolean;
  createdSpan: () => { date: string; timeRange: string } | null;
  createdViewerSpan: () => { date: string; timeRange: string } | null;
  createdDurationLabel: () => string;
  approverNames: () => string[];
  approvalExpiryLabel: () => string | null;
  mode: () => 'oneOff' | 'recurring';
  setMode(mode: 'oneOff' | 'recurring'): void;
  recurrence: () => RecurrenceFormValue;
  recurrenceErrors: () => RecurrenceFormErrors;
  recurrenceIsValid: () => boolean;
  recurrencePatternLabel: () => string;
  recurrenceEndsLabel: () => string;
  setFrequency(event: Event): void;
  setIntervalValue(event: Event): void;
  setLocalStartTime(event: Event): void;
  setLocalEndTime(event: Event): void;
  setStartDate(event: Event): void;
  setEndCondition(kind: 'endDate' | 'occurrenceCount'): void;
  setEndDate(event: Event): void;
  setOccurrenceCount(event: Event): void;
  seriesOutcome: () => { recurrenceRuleId: string | null; occurrences: unknown[] } | null;
  seriesSummary: () => { created: number; skipped: number; refused: number; total: number } | null;
  seriesSummaryLine: () => string;
  seriesOutcomeHeading: () => string;
  seriesCreatedNothing: () => boolean;
  canRetrySeries: () => boolean;
  editSeriesAgain(): void;
  recurrenceUnavailable: () => string | null;
  recurrenceUnavailableMessage: () => string | null;
  recurrenceStartDateError: () => string | null;
  recurrenceIntervalError: () => string | null;
  recurrenceTimesError: () => string | null;
  recurrenceDurationError: () => string | null;
  recurrenceOccurrenceCountError: () => string | null;
  recurrenceEndDateError: () => string | null;
  submittedPatternLabel: () => string;
  submittedTimesLabel: () => string;
};

// Open 09:00-17:00 Monday to Friday, like the seeded resources. Not an empty
// list any more: since the 2026-09-17 pass a resource with *no* published
// hours is an explicit "recurring bookings aren't available" state (a series
// against it could only ever produce OutsideAvailability occurrences), so an
// empty schedule is now a case to opt into rather than the default every
// recurring test would silently inherit.
const WEEKDAY_WINDOWS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'].map((weekday, index) => ({
  id: `w${index + 1}`,
  weekday: weekday as ResourceDetail['availabilityWindows'][number]['weekday'],
  opensAt: '09:00:00',
  closesAt: '17:00:00',
}));

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
    availabilityWindows: WEEKDAY_WINDOWS,
    approvers: [],
    ...overrides,
  };
}

// The query string the availability screen navigates with.
// The component reads control values off the event target, so these stand in
// for a real input/select change.
function inputEvent(value: string): Event {
  const input = document.createElement('input');
  input.value = value;
  return { target: input } as unknown as Event;
}

function selectEvent(value: string): Event {
  const select = document.createElement('select');
  const option = document.createElement('option');
  option.value = value;
  select.appendChild(option);
  select.value = value;
  return { target: select } as unknown as Event;
}

const selectionParams = {
  startUtc: '2026-09-24T13:15:00Z',
  endUtc: '2026-09-24T15:30:00Z',
  quantity: '1',
};

// The calendar deep link names the booking's date in the *viewer's* zone
// (wp7-plan.md §3's display default), so a hard-coded "2026-09-24" would be a
// quietly timezone-dependent assertion: it holds in UTC (where CI runs) and in
// CET (where this is usually written), and fails east of about UTC+11. Derived
// here rather than imported from calendar-range.ts, so the test is not simply
// restating the implementation it is checking.
function viewerLocalDateOf(utcIso: string): string {
  const d = new Date(utcIso);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

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
        // A real Router, so the tests that actually render the template (the
        // outcome panel's own links) get working RouterLinks. The
        // ActivatedRoute stub below still wins for the route params, being
        // provided last.
        provideRouter([]),
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
        expect(component.rejection()).toBeNull();
      });

      it('surfaces a rejection without retrying it', () => {
        const component = loaded();

        component.confirmBooking();
        httpMock.expectOne(`${API}/bookings`).flush(
          { title: 'Conflict', status: 409, reasonCode: 'SlotUnavailable', correlationId: 'c1' },
          { status: 409, statusText: 'Conflict' },
        );

        expect(component.rejection()?.formMessage).toContain('booked this time while you were filling in the form');
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

      // Found by the owner, 2026-09-17: a hand-edited `?quantity=16` on the
      // single-unit 3D Printer booked *one* unit and said nothing, because
      // this was briefly clamped to capacity. Silently booking something
      // other than what was asked for is worse than refusing it — decision
      // `0015`'s own "reject, don't clamp" instinct — so the value is kept
      // and refused instead.
      it('refuses a URL quantity larger than the capacity instead of quietly booking fewer', () => {
        const component = loaded({ capacity: 1 }, { ...selectionParams, quantity: '16' });

        expect(component.quantity()).toBe(16);
        expect(component.capacityError()).toContain('single unit');
        expect(component.capacityError()).toContain('16');
        expect(component.canSubmit()).toBe(false);

        component.confirmBooking();
        httpMock.expectNone(`${API}/bookings`);
      });

      it('words it with the real capacity for a pooled resource', () => {
        const component = loaded({ capacity: 4 }, { ...selectionParams, quantity: '9' });

        expect(component.capacityError()).toBe('This resource has 4 units, but 9 were asked for.');
        expect(component.canSubmit()).toBe(false);
      });

      // With a stepper on screen the member can fix it themselves, so the
      // message belongs against that control rather than at the top of the
      // form — and stepping back within capacity clears it.
      it('places the message against the stepper when there is one', () => {
        const component = loaded({ capacity: 4 }, { ...selectionParams, quantity: '5' });

        expect(component.quantityError()).not.toBeNull();
        expect(component.topOfFormMessage()).toBeNull();

        component.decrementQuantity();

        expect(component.quantityError()).toBeNull();
        expect(component.canSubmit()).toBe(true);
      });

      // An exclusive resource renders no stepper at all (decision `0005`), so
      // a field-level message would have nothing to attach to — it goes to the
      // top of the form, with the one control that can actually fix it.
      it('places it at the top of the form, with a way out, when there is no stepper', () => {
        const fixture = createFixture('r1', { ...selectionParams, quantity: '16' });
        const component = fixture.componentInstance as TestableBookingComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ capacity: 1 }));
        fixture.detectChanges();

        expect(component.showQuantityStepper()).toBe(false);
        expect(component.topOfFormMessage()).toContain('single unit');
        expect(component.topOfFormRecheck()).toBe(true);

        const root = fixture.nativeElement as HTMLElement;
        expect(root.querySelector('.submit-error')?.textContent).toContain('single unit');
        expect(
          Array.from(root.querySelectorAll('.submit-error a')).map((a) => a.getAttribute('href')),
        ).toEqual(['/resources/r1/availability']);
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

  // Step 6: the recurring half. The form's own arithmetic and guards are
  // covered in recurrence-form.spec.ts (and checked against the live
  // validator's own boundaries); these are about the toggle, the pre-fill, and
  // what the screen does with the result.
  describe('the recurring toggle', () => {
    function loadedForRecurring(
      detailOverrides: Partial<ResourceDetail> = {},
      queryParams: Record<string, string> = selectionParams,
    ) {
      const fixture = createFixture('r1', queryParams);
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', ...detailOverrides }));
      return { fixture, component };
    }

    it('starts on the one-off half', () => {
      const { component } = loadedForRecurring();
      expect(component.mode()).toBe('oneOff');
    });

    // The entry point: requiring a picked slot first made the availability
    // screen a toll booth for recurring bookings, since a series names its own
    // schedule and uses none of the slot's instants (owner's call,
    // 2026-09-17).
    describe('arriving straight from the resource page', () => {
      beforeEach(() => {
        vi.useFakeTimers({ toFake: ['Date'] });
        // A Saturday — the seeded resources open Monday to Friday.
        vi.setSystemTime(new Date('2026-09-19T12:00:00Z'));
      });

      afterEach(() => {
        vi.useRealTimers();
      });

      it('opens on the recurring half with no slot at all', () => {
        const fixture = createFixture('r1', { mode: 'recurring' });
        const component = fixture.componentInstance as TestableBookingComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({
            timeZoneId: 'UTC',
            minDurationMinutes: 30,
            availabilityWindows: [
              { id: 'w1', weekday: 'Monday', opensAt: '09:00:00', closesAt: '17:00:00' },
              { id: 'w2', weekday: 'Thursday', opensAt: '09:00:00', closesAt: '17:00:00' },
            ],
          }),
        );

        expect(component.selection()).toBeNull();
        expect(component.mode()).toBe('recurring');
        // Seeded from the resource's own schedule: the next open day, at that
        // day's opening time, for the shortest length it allows.
        expect(component.recurrence()).toMatchObject({
          startDate: '2026-09-21',
          localStartTime: '09:00',
          localEndTime: '09:30',
        });
        // And it opens valid, rather than on a blank that fails its own guards.
        expect(component.recurrenceErrors()).toEqual({});
        expect(component.recurrenceIsValid()).toBe(true);
      });

      it('asks for a slot only for the one-off half, keeping the toggle reachable', () => {
        const fixture = createFixture('r1', {});
        const component = fixture.componentInstance as TestableBookingComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
        fixture.detectChanges();
        const root = fixture.nativeElement as HTMLElement;

        expect(component.mode()).toBe('oneOff');
        expect(root.querySelector('.pick-first')).not.toBeNull();
        expect(component.canSubmit()).toBe(false);
        // The toggle is what makes this state escapable.
        expect(root.querySelectorAll('.mode-toggle button').length).toBe(2);

        component.setMode('recurring');
        fixture.detectChanges();

        expect(root.querySelector('.pick-first')).toBeNull();
        expect(root.querySelector('fieldset.recurrence')).not.toBeNull();
        expect(component.recurrenceIsValid()).toBe(true);
      });
    });

    // The window guard is what lets the times stay editable: a time outside
    // the resource's hours would have every occurrence refused, and the
    // windows are already loaded here.
    it('refuses a time the resource is closed at, before any request', () => {
      const { component } = loadedForRecurring({
        availabilityWindows: [{ id: 'w1', weekday: 'Thursday', opensAt: '09:00:00', closesAt: '17:00:00' }],
      });
      component.setMode('recurring');

      component.setLocalStartTime(inputEvent('07:00'));
      component.setLocalEndTime(inputEvent('08:00'));

      expect(component.recurrenceErrors().times).toContain('Thursday');
      expect(component.recurrenceIsValid()).toBe(false);
    });

    // The selection is the one thing both halves share, so switching must not
    // throw away the time already picked.
    it('pre-fills the series from the selected slot, in the resource\'s timezone', () => {
      const { component } = loadedForRecurring({ timeZoneId: 'America/New_York' });

      component.setMode('recurring');

      // 13:15-15:30Z on 2026-09-24 is 09:15-11:30 in New York, same date.
      expect(component.recurrence()).toMatchObject({
        startDate: '2026-09-24',
        localStartTime: '09:15',
        localEndTime: '11:30',
        frequency: 'Weekly',
        intervalValue: 1,
        endCondition: 'occurrenceCount',
      });
    });

    it('re-seeds when a different slot arrives', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');
      component.setFrequency(selectEvent('Daily'));
      expect(component.recurrence().frequency).toBe('Daily');

      queryParamMap$.next(
        convertToParamMap({
          startUtc: '2026-10-01T08:00:00Z',
          endUtc: '2026-10-01T09:00:00Z',
          quantity: '1',
        }),
      );

      expect(component.recurrence()).toMatchObject({
        startDate: '2026-10-01',
        localStartTime: '08:00',
        localEndTime: '09:00',
        // Back to the default, since the new slot seeds the whole group.
        frequency: 'Weekly',
      });
    });

    it('keeps edits until something re-seeds it', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');

      component.setFrequency(selectEvent('Monthly'));
      component.setIntervalValue(inputEvent('3'));
      component.setOccurrenceCount(inputEvent('6'));

      expect(component.recurrence()).toMatchObject({
        frequency: 'Monthly',
        intervalValue: 3,
        occurrenceCount: 6,
      });
      expect(component.recurrenceErrors()).toEqual({});
    });

    it('surfaces a bad end time against the times, not the form', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');

      component.setLocalEndTime(inputEvent('08:00'));

      expect(component.recurrenceErrors().times).toContain('after the start time');
      expect(component.recurrenceIsValid()).toBe(false);
    });

    it('surfaces a span past the two-year cap against the end condition', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');

      component.setEndCondition('endDate');
      component.setEndDate(inputEvent('2029-01-01'));

      expect(component.recurrenceErrors().endDate).toContain('2 years');
      expect(component.recurrenceIsValid()).toBe(false);
    });

    it('describes what the series implies', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');
      component.setIntervalValue(inputEvent('2'));

      expect(component.recurrencePatternLabel()).toBe('Every 2 weeks');
      // 4 occurrences, 2-week interval, from 2026-09-24 -> last on 2026-11-05.
      expect(component.recurrenceEndsLabel()).toContain('4 occurrences');
      expect(component.recurrenceEndsLabel()).toContain('Nov 5, 2026');

      component.setEndCondition('endDate');
      component.setEndDate(inputEvent('2026-12-24'));
      expect(component.recurrenceEndsLabel()).toBe('On Thu, Dec 24, 2026');
    });

    // Never through the one-off endpoint: that would create a single booking
    // for a member who asked for a series.
    it('submits a series to its own endpoint, never to /bookings', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');

      expect(component.recurrenceIsValid()).toBe(true);
      component.confirmBooking();

      httpMock.expectNone(`${API}/bookings`);
      const req = httpMock.expectOne(`${API}/recurrence-rules`);
      expect(req.request.method).toBe('POST');
      req.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
    });

    it('blocks a submit while the series form has an error', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');
      component.setLocalEndTime(inputEvent('08:00'));

      expect(component.canSubmit()).toBe(false);
      component.confirmBooking();
      httpMock.expectNone(`${API}/recurrence-rules`);
    });

    it('can still submit the one-off half after switching back', () => {
      const { component } = loadedForRecurring();
      component.setMode('recurring');
      component.setMode('oneOff');

      expect(component.canSubmit()).toBe(true);
      component.confirmBooking();
      httpMock.expectOne(`${API}/bookings`).flush(
        {
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
        },
        { status: 201, statusText: 'Created' },
      );
      expect(component.created()).not.toBeNull();
    });

    it('shows the recurring fields under the title, and hides the single slot\'s own', () => {
      const { fixture, component } = loadedForRecurring();
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;

      expect(root.querySelector('fieldset.recurrence')).toBeNull();
      expect(root.querySelector('.readonly-field')).not.toBeNull();

      component.setMode('recurring');
      fixture.detectChanges();

      const recurrence = root.querySelector('fieldset.recurrence');
      expect(recurrence).not.toBeNull();
      // The single slot's read-only Date/Time/Duration are gone: the series
      // names its own, and showing both would be the same fact twice.
      expect(root.querySelector('.readonly-field')).toBeNull();
      // Weekly takes no weekday picker — the copy says where the weekday comes
      // from rather than leaving one to hunt for.
      expect(recurrence?.textContent).toContain('same weekday as the start date');
    });
  });

  // Step 7: the recurring submit, its idempotency key, and the per-occurrence
  // report (FR-5.4). The report's own shaping is covered in
  // recurrence-outcome.spec.ts; these are about what the screen does.
  describe('submitting a series', () => {
    function readyToSubmit(detailOverrides: Partial<ResourceDetail> = {}) {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', ...detailOverrides }));
      component.setMode('recurring');
      return { fixture, component };
    }

    function occurrence(occurrenceDate: string, status: string, reasonCode: string | null = null) {
      return {
        occurrenceDate,
        status,
        bookingId: status === 'Created' ? 'b1' : null,
        reasonCode,
      };
    }

    it('sends the form as local wall clock, with an idempotency key', () => {
      const { component } = readyToSubmit();
      component.setOccurrenceCount(inputEvent('3'));

      component.confirmBooking();

      const req = httpMock.expectOne(`${API}/recurrence-rules`);
      expect(req.request.body).toMatchObject({
        resourceId: 'r1',
        frequency: 'Weekly',
        intervalValue: 1,
        localStartTime: '13:15:00',
        localEndTime: '15:30:00',
        startDate: '2026-09-24',
        occurrenceCount: 3,
        quantity: 1,
      });
      expect(req.request.headers.get('Idempotency-Key')).toBeTruthy();
      expect(component.submitting()).toBe(true);

      req.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
      expect(component.submitting()).toBe(false);
    });

    it('keeps the per-occurrence report from a 201', () => {
      // Instant confirmation, so the summary reads "booked" — the approval
      // wording has its own tests below.
      const { component } = readyToSubmit({ requiresApproval: false });
      component.confirmBooking();

      httpMock.expectOne(`${API}/recurrence-rules`).flush(
        {
          recurrenceRuleId: 'rr1',
          occurrences: [
            occurrence('2026-09-24', 'Created'),
            occurrence('2026-10-01', 'Refused', 'SlotUnavailable'),
            occurrence('2026-10-08', 'Created'),
          ],
        },
        { status: 201, statusText: 'Created' },
      );

      expect(component.seriesOutcome()?.recurrenceRuleId).toBe('rr1');
      expect(component.seriesSummary()).toMatchObject({ created: 2, refused: 1, total: 3 });
      expect(component.seriesSummaryLine()).toBe('2 booked, 1 refused');
      expect(component.seriesCreatedNothing()).toBe(false);
    });

    // The all-refused 422 is not an error to report as one: it carries the same
    // breakdown a 201 does, and that breakdown is the answer (FR-5.4).
    it('renders an all-refused 422 through the same report', () => {
      const { component } = readyToSubmit();
      component.confirmBooking();

      httpMock.expectOne(`${API}/recurrence-rules`).flush(
        {
          title: 'The request was rejected by a rule.',
          status: 422,
          reasonCode: 'NoOccurrencesCreated',
          correlationId: 'c1',
          occurrences: [
            occurrence('2026-09-24', 'Refused', 'OutsideAvailability'),
            occurrence('2026-10-01', 'Refused', 'OutsideAvailability'),
          ],
        },
        { status: 422, statusText: 'Unprocessable Content' },
      );

      expect(component.seriesCreatedNothing()).toBe(true);
      expect(component.seriesOutcome()?.occurrences).toHaveLength(2);
      // Not routed through the reason-code catalogue — there is nothing
      // generic to say about it.
      expect(component.rejection()).toBeNull();
    });

    it('renders both framings of the same payload in the DOM', () => {
      const { fixture, component } = readyToSubmit({ requiresApproval: false });
      component.confirmBooking();
      httpMock.expectOne(`${API}/recurrence-rules`).flush(
        {
          recurrenceRuleId: 'rr1',
          occurrences: [
            occurrence('2026-09-24', 'Created'),
            occurrence('2027-03-14', 'SkippedSpringForwardGap'),
            occurrence('2026-10-01', 'Refused', 'BlackoutPeriod'),
          ],
        },
        { status: 201, statusText: 'Created' },
      );
      fixture.detectChanges();

      const root = fixture.nativeElement as HTMLElement;
      expect(root.textContent).toContain('Series booked');
      const rows = Array.from(root.querySelectorAll('.occurrence'));
      expect(rows).toHaveLength(3);
      expect(rows[0].textContent).toContain('Thu, Sep 24, 2026');
      expect(rows[0].textContent).toContain('Booked');
      expect(rows[1].textContent).toContain('does not exist on that date');
      expect(rows[2].textContent).toContain('Blackout period');
      // The form is gone: the series exists now.
      expect(root.querySelector('fieldset.recurrence')).toBeNull();
    });

    // FR-7.1, one screen over from where the one-off panel already says it.
    // Every occurrence of a series on an approval-gated resource is created
    // `Pending` — `CreateRecurrenceSeriesCommandRequestHandler` picks the
    // status from `resource.RequiresApproval` — so "Series booked" and
    // "Booked" against each date claimed times that are not held for anyone.
    describe('wording for an approval-gated resource', () => {
      function submitOne(requiresApproval: boolean) {
        const { fixture, component } = readyToSubmit({ requiresApproval });
        component.confirmBooking();
        httpMock.expectOne(`${API}/recurrence-rules`).flush(
          {
            recurrenceRuleId: 'rr1',
            occurrences: [occurrence('2026-09-24', 'Created'), occurrence('2026-10-01', 'Created')],
          },
          { status: 201, statusText: 'Created' },
        );
        fixture.detectChanges();
        return { component, root: fixture.nativeElement as HTMLElement };
      }

      it('calls a series on an instant-confirmation resource booked', () => {
        const { component, root } = submitOne(false);

        expect(component.seriesOutcomeHeading()).toBe('Series booked');
        expect(root.querySelector('h2:not(.summary-text h2)')?.textContent).toContain('Series booked');
        expect(component.seriesSummaryLine()).toBe('2 booked');
        expect(Array.from(root.querySelectorAll('.occurrence-reason')).map((n) => n.textContent?.trim())).toEqual([
          'Booked',
          'Booked',
        ]);
        expect(root.textContent).not.toContain('not held for you yet');
      });

      it('calls the same series submitted, and each date pending, when approval is required', () => {
        const { component, root } = submitOne(true);

        expect(component.seriesOutcomeHeading()).toBe('Series submitted');
        expect(root.textContent).toContain('Series submitted');
        expect(root.textContent).not.toContain('Series booked');
        expect(component.seriesSummaryLine()).toBe('2 requested');
        expect(Array.from(root.querySelectorAll('.occurrence-reason')).map((n) => n.textContent?.trim())).toEqual([
          'Pending approval',
          'Pending approval',
        ]);
        // The same sentence the one-off panel uses for a Pending booking.
        expect(root.textContent).toContain('not held for you yet');
        expect(root.textContent).toContain('needs approval');
      });
    });

    // The all-refused panel tells the member to adjust the series, so it has
    // to be able to hand the form back — with what they typed still in it.
    it('hands the form back, unchanged, from an all-refused series', () => {
      const { fixture, component } = readyToSubmit();
      component.setOccurrenceCount(inputEvent('7'));
      component.confirmBooking();
      httpMock.expectOne(`${API}/recurrence-rules`).flush(
        {
          title: 'x',
          status: 422,
          reasonCode: 'NoOccurrencesCreated',
          correlationId: 'c1',
          occurrences: [occurrence('2026-09-24', 'Refused', 'OutsideAvailability')],
        },
        { status: 422, statusText: 'Unprocessable Content' },
      );
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('fieldset.recurrence')).toBeNull();

      component.editSeriesAgain();
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).querySelector('fieldset.recurrence')).not.toBeNull();
      expect(component.recurrence().occurrenceCount).toBe(7);
      expect(component.canSubmit()).toBe(true);
    });

    it('routes an ordinary refusal through the reason-code catalogue', () => {
      const { component } = readyToSubmit();
      component.confirmBooking();

      httpMock.expectOne(`${API}/recurrence-rules`).flush(
        { title: 'x', status: 422, reasonCode: 'ResourceArchived', correlationId: 'c1' },
        { status: 422, statusText: 'Unprocessable Content' },
      );

      expect(component.seriesOutcome()).toBeNull();
      expect(component.rejection()?.formMessage).toContain('archived');
    });

    // The lifecycle that fails in both directions if it is got backwards:
    // reuse too eagerly and a deliberate second series resolves to the first;
    // regenerate on a retry and a crash-resumed request creates a duplicate.
    describe('the idempotency key', () => {
      function keyOf(request: { headers: { get(name: string): string | null } }): string {
        return request.headers.get('Idempotency-Key') ?? '';
      }

      it('is reused when the same attempt is retried after an unobservable outcome', () => {
        const { component } = readyToSubmit();

        component.confirmBooking();
        const first = httpMock.expectOne(`${API}/recurrence-rules`);
        const firstKey = keyOf(first.request);
        first.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

        expect(component.canRetrySeries()).toBe(true);

        component.confirmBooking();
        const retry = httpMock.expectOne(`${API}/recurrence-rules`);
        expect(keyOf(retry.request)).toBe(firstKey);

        retry.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
      });

      // "The same attempt" is decided by the body, so editing anything makes
      // the next submit a new one without a dirty flag to maintain.
      it('is regenerated once the form changes', () => {
        const { component } = readyToSubmit();

        component.confirmBooking();
        const first = httpMock.expectOne(`${API}/recurrence-rules`);
        const firstKey = keyOf(first.request);
        first.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

        component.setOccurrenceCount(inputEvent('8'));
        component.confirmBooking();

        const second = httpMock.expectOne(`${API}/recurrence-rules`);
        expect(keyOf(second.request)).not.toBe(firstKey);
        second.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
      });

      // A refusal created nothing, so a later submit is a fresh attempt —
      // reusing the key there would resolve a genuinely new request to the
      // operation that already failed.
      it('is regenerated after a definitive refusal', () => {
        const { component } = readyToSubmit();

        component.confirmBooking();
        const first = httpMock.expectOne(`${API}/recurrence-rules`);
        const firstKey = keyOf(first.request);
        first.flush(
          {
            title: 'x',
            status: 422,
            reasonCode: 'NoOccurrencesCreated',
            correlationId: 'c1',
            occurrences: [occurrence('2026-09-24', 'Refused', 'OutsideAvailability')],
          },
          { status: 422, statusText: 'Unprocessable Content' },
        );

        // The outcome panel replaces the form, so getting back to it is part
        // of the flow — "Adjust the series" is what the panel offers.
        component.editSeriesAgain();
        component.setOccurrenceCount(inputEvent('2'));
        component.confirmBooking();

        const second = httpMock.expectOne(`${API}/recurrence-rules`);
        expect(keyOf(second.request)).not.toBe(firstKey);
        second.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
      });

      // The key's promise is per *body*: `createSeries` reuses a key only for
      // a byte-identical request. So an edit after an unobservable outcome
      // means the next submit mints a fresh key and could create a second
      // series — while the screen was still saying "trying again is safe".
      it('withdraws the safe retry once the form no longer matches the pending attempt', () => {
        const { fixture, component } = readyToSubmit();

        component.confirmBooking();
        httpMock
          .expectOne(`${API}/recurrence-rules`)
          .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
        fixture.detectChanges();

        expect(component.canRetrySeries()).toBe(true);
        let root = fixture.nativeElement as HTMLElement;
        expect(root.querySelector('.submit-error button')?.textContent).toContain('Try again');

        component.setOccurrenceCount(inputEvent('9'));
        fixture.detectChanges();

        expect(component.canRetrySeries()).toBe(false);
        root = fixture.nativeElement as HTMLElement;
        expect(root.querySelector('.submit-error button')).toBeNull();
        expect(root.querySelector('.submit-error')?.textContent).not.toContain('Trying again is safe');
        // The honest answer instead: go and look — and, since WP-7 Phase 4, at
        // a named period rather than at a list. A series has no single date
        // worth singling out, so it points at the month its start date is in.
        const checkHrefs = Array.from(root.querySelectorAll('.submit-error a')).map((a) =>
          a.getAttribute('href'),
        );
        expect(checkHrefs.some((href) => href?.startsWith('/calendar?view=month&date='))).toBe(true);
      });

      // ...and putting it back the way it was restores the guarantee, since
      // the body is once again the one the key was minted for.
      it('restores the safe retry when the form is returned to the submitted values', () => {
        const { component } = readyToSubmit();

        component.confirmBooking();
        const first = httpMock.expectOne(`${API}/recurrence-rules`);
        const firstKey = keyOf(first.request);
        first.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

        component.setOccurrenceCount(inputEvent('9'));
        expect(component.canRetrySeries()).toBe(false);

        component.setOccurrenceCount(inputEvent('4'));
        expect(component.canRetrySeries()).toBe(true);

        component.confirmBooking();
        const retry = httpMock.expectOne(`${API}/recurrence-rules`);
        expect(keyOf(retry.request)).toBe(firstKey);
        retry.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
      });

      // The limitation this pass chose to state rather than engineer away:
      // the key lives in this component, so a reload or a return visit starts
      // a genuinely new attempt. The copy says so rather than implying a
      // replay that would not happen.
      it('says the guarantee is limited to this page', () => {
        const { fixture, component } = readyToSubmit();

        component.confirmBooking();
        httpMock
          .expectOne(`${API}/recurrence-rules`)
          .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
        fixture.detectChanges();

        const text = (fixture.nativeElement as HTMLElement).querySelector('.submit-error')?.textContent ?? '';
        expect(text).toContain('while this page stays open');
        expect(text).toContain('check your calendar');
      });

      it('offers a retry only for the recurring half', () => {
        const fixture = createFixture('r1');
        const component = fixture.componentInstance as TestableBookingComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));

        component.confirmBooking();
        httpMock
          .expectOne(`${API}/bookings`)
          .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

        expect(component.rejection()?.mayHaveBeenCreated).toBe(true);
        // No key on POST /bookings, so no safe retry to offer (§7).
        expect(component.canRetrySeries()).toBe(false);
      });
    });
  });

  // Finding 2 of the 2026-09-17 pass: a series' validation failures were
  // being read through the one-off form's field map, which knows title,
  // quantity and duration and calls everything else "go back to availability".
  describe('a series refused by the server', () => {
    function submitAndFail(status: number, body: object, detailOverrides: Partial<ResourceDetail> = {}) {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', ...detailOverrides }));
      component.setMode('recurring');

      component.confirmBooking();
      httpMock.expectOne(`${API}/recurrence-rules`).flush(body, { status, statusText: 'Error' });
      fixture.detectChanges();

      return { fixture, component, root: fixture.nativeElement as HTMLElement };
    }

    function validationBody(errors: Record<string, string[]>) {
      return { title: 'Rejected', status: 400, reasonCode: 'ValidationFailed', correlationId: 'c1', errors };
    }

    it('puts an occurrence-count failure against that control, not at the top of the form', () => {
      const { component, root } = submitAndFail(
        400,
        validationBody({ OccurrenceCount: ['OccurrenceCount must be greater than zero.'] }),
      );

      expect(component.recurrenceOccurrenceCountError()).toBe('OccurrenceCount must be greater than zero.');
      expect(component.topOfFormMessage()).toBeNull();
      expect(root.querySelector('fieldset.recurrence')?.textContent).toContain(
        'OccurrenceCount must be greater than zero.',
      );
    });

    it('puts a start-date failure against the start date', () => {
      const { component, root } = submitAndFail(400, validationBody({ StartDate: ['StartDate is out of range.'] }));

      expect(component.recurrenceStartDateError()).toBe('StartDate is out of range.');
      expect(root.querySelector('fieldset.recurrence')?.textContent).toContain('StartDate is out of range.');
    });

    it('puts a local-time failure against the From/To pair', () => {
      const { component } = submitAndFail(
        400,
        validationBody({ LocalEndTime: ['LocalEndTime must be after LocalStartTime.'] }),
      );

      expect(component.recurrenceTimesError()).toBe('LocalEndTime must be after LocalStartTime.');
    });

    // The duration refusal a series really can get — and which had nowhere on
    // this half of the form to appear: `durationError` renders inside the
    // one-off panel, which recurring mode hides entirely.
    it('shows a server duration refusal on the recurring half, where the member can see it', () => {
      const { component, root } = submitAndFail(422, {
        title: 'Rejected',
        status: 422,
        reasonCode: 'BookingDurationOutOfRange',
        correlationId: 'c1',
      });

      expect(component.recurrenceDurationError()).toContain('outside what the resource allows');
      expect(root.querySelector('fieldset.recurrence')?.textContent).toContain('outside what the resource allows');
    });

    it('never tells the member to re-pick a slot the series does not have', () => {
      const { component, root } = submitAndFail(400, validationBody({ IntervalValue: ['Must be positive.'] }));

      expect(component.topOfFormRecheck()).toBe(false);
      expect(
        Array.from(root.querySelectorAll('.submit-error a')).map((a) => a.getAttribute('href')),
      ).not.toContain('/resources/r1/availability');
    });

    // Precedence, and the deadlock avoided by clearing it: a server message
    // wins over the client's own for the same control (as `titleError`
    // already does), it blocks the submit, and editing that control hands the
    // form back rather than leaving it permanently unsubmittable — a
    // rejection is otherwise only cleared *by* the submit it is blocking.
    it('lets the server message win, then clears it when the control is edited', () => {
      const { component } = submitAndFail(400, validationBody({ IntervalValue: ['Server refused this interval.'] }));

      expect(component.recurrenceIntervalError()).toBe('Server refused this interval.');
      expect(component.recurrenceIsValid()).toBe(false);
      expect(component.canSubmit()).toBe(false);

      component.setIntervalValue(inputEvent('0'));
      expect(component.recurrenceIntervalError()).toContain('whole number of 1 or more');

      component.setIntervalValue(inputEvent('2'));
      expect(component.recurrenceIntervalError()).toBeNull();
      expect(component.canSubmit()).toBe(true);

      component.confirmBooking();
      httpMock
        .expectOne(`${API}/recurrence-rules`)
        .flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
    });

    // An edit to the recurring group says nothing about whether the previous
    // request committed, so the unknown-outcome warning is not swept away
    // with the field messages.
    it('keeps the unknown-outcome warning when a field is edited', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      component.setMode('recurring');

      component.confirmBooking();
      httpMock
        .expectOne(`${API}/recurrence-rules`)
        .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      component.setOccurrenceCount(inputEvent('5'));

      expect(component.rejection()?.mayHaveBeenCreated).toBe(true);
      expect(component.topOfFormMessage()).toContain('series may have been created');
    });
  });

  // Finding 3: the Confirm button was disabled while a request was in flight,
  // but every field feeding it stayed editable — so the request that went out
  // and the form on screen could describe two different series.
  describe('while a series submit is in flight', () => {
    function submitting() {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', requiresApproval: false }));
      component.setMode('recurring');
      component.confirmBooking();
      const request = httpMock.expectOne(`${API}/recurrence-rules`);
      fixture.detectChanges();

      return { fixture, component, request, root: fixture.nativeElement as HTMLElement };
    }

    it('disables every control that feeds the request', () => {
      const { component, request, root } = submitting();

      expect(component.submitting()).toBe(true);
      // One `disabled` on the fieldset covers the whole recurring group.
      expect(root.querySelector<HTMLFieldSetElement>('fieldset.recurrence')?.disabled).toBe(true);
      expect(root.querySelector<HTMLInputElement>('#booking-title')?.disabled).toBe(true);
      expect(
        Array.from(root.querySelectorAll<HTMLButtonElement>('.mode-toggle button')).map((b) => b.disabled),
      ).toEqual([true, true]);
      expect(root.querySelector<HTMLButtonElement>('button.confirm-button')?.disabled).toBe(true);

      request.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
    });

    it('disables the quantity stepper too', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity: 4 }));
      component.setMode('recurring');
      component.confirmBooking();
      const request = httpMock.expectOne(`${API}/recurrence-rules`);
      fixture.detectChanges();

      expect(
        Array.from(
          (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.stepper button'),
        ).map((b) => b.disabled),
      ).toEqual([true, true]);

      request.flush({ recurrenceRuleId: 'rr1', occurrences: [] }, { status: 201, statusText: 'Created' });
    });

    // Belt and braces behind the disabled fields: the panel is built from the
    // values the request was built from, so even a write that reached the
    // form behind the submit cannot make the confirmation describe a series
    // nobody booked.
    it('describes the series that was submitted, not the form as it stands', () => {
      const { fixture, component, request, root } = submitting();

      component.setFrequency(selectEvent('Monthly'));
      component.setLocalStartTime(inputEvent('08:00'));
      component.setLocalEndTime(inputEvent('09:00'));

      request.flush(
        { recurrenceRuleId: 'rr1', occurrences: [{ occurrenceDate: '2026-09-24', status: 'Created', bookingId: 'b1', reasonCode: null }] },
        { status: 201, statusText: 'Created' },
      );
      fixture.detectChanges();

      expect(component.submittedPatternLabel()).toBe('Every week');
      expect(component.submittedTimesLabel()).toBe('13:15–15:30');
      const lead = root.querySelector('.outcome-lead')?.textContent ?? '';
      expect(lead).toContain('Every week');
      expect(lead).toContain('13:15–15:30');
      expect(lead).not.toContain('Every month');
      expect(lead).not.toContain('08:00');
    });
  });

  // Finding 6: an active resource with no published hours still offered a
  // recurring form. `outsideOpeningHours` deliberately says nothing when there
  // are no windows to judge against, so every field validated — and every
  // occurrence of the resulting series was guaranteed OutsideAvailability.
  describe('a resource with no bookable hours', () => {
    function loadedRecurring(detailOverrides: Partial<ResourceDetail>) {
      const fixture = createFixture('r1', { mode: 'recurring' });
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', ...detailOverrides }));
      fixture.detectChanges();

      return { fixture, component, root: fixture.nativeElement as HTMLElement };
    }

    it('refuses to offer a series, and says why, when no hours are published', () => {
      const { component, root } = loadedRecurring({ availabilityWindows: [] });

      expect(component.recurrenceUnavailable()).toBe('noOpeningHours');
      expect(component.canSubmit()).toBe(false);
      expect(root.querySelector('fieldset.recurrence')).toBeNull();
      expect(root.querySelector('.recurrence-unavailable')?.textContent).toContain(
        'no bookable hours configured',
      );
      expect(root.querySelector<HTMLButtonElement>('button.confirm-button')?.disabled).toBe(true);
      // And the advice is not "fix the highlighted fields" — there are none.
      expect(root.textContent).not.toContain('Fix the highlighted fields');

      component.confirmBooking();
      httpMock.expectNone(`${API}/recurrence-rules`);
    });

    it('says so too when no window is long enough for the shortest booking allowed', () => {
      const { component, root } = loadedRecurring({
        minDurationMinutes: 600,
        maxDurationMinutes: null,
        availabilityWindows: [{ id: 'w1', weekday: 'Monday', opensAt: '09:00:00', closesAt: '17:00:00' }],
      });

      expect(component.recurrenceUnavailable()).toBe('noBookableWindow');
      expect(root.querySelector('.recurrence-unavailable')?.textContent).toContain('long enough');
      expect(component.canSubmit()).toBe(false);
    });

    it('says so when the duration limits contradict each other', () => {
      const { component } = loadedRecurring({ minDurationMinutes: 120, maxDurationMinutes: 60 });

      expect(component.recurrenceUnavailable()).toBe('durationLimitsConflict');
      expect(component.recurrenceUnavailableMessage()).toContain('longer than its longest');
    });

    // Not conflated with "nothing is free": a resource with hours that happen
    // to be fully booked still gets a form, submits, and receives FR-5.4's
    // per-occurrence answer.
    it('offers the form for a resource that has hours at all', () => {
      const { component, root } = loadedRecurring({});

      expect(component.recurrenceUnavailable()).toBeNull();
      expect(root.querySelector('fieldset.recurrence')).not.toBeNull();
      expect(root.querySelector('.recurrence-unavailable')).toBeNull();
    });

    // The one-off half is unaffected: a slot picked on the availability screen
    // is the server's own answer about what is bookable.
    it('leaves the one-off half alone', () => {
      const fixture = createFixture('r1', selectionParams);
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock
        .expectOne(`${API}/resources/r1`)
        .flush(fakeDetail({ timeZoneId: 'UTC', availabilityWindows: [] }));

      expect(component.mode()).toBe('oneOff');
      expect(component.canSubmit()).toBe(true);
    });
  });

  // Finding 1, as the screen sees it: these values reached `Date` arithmetic
  // inside a `computed` the template reads, so the failure was a RangeError
  // thrown during change detection rather than a validation message.
  describe('extreme or cleared values in the recurring form', () => {
    function recurringForm() {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      component.setMode('recurring');
      fixture.detectChanges();
      return { fixture, component };
    }

    it('renders a cleared start date as a message rather than throwing', () => {
      const { fixture, component } = recurringForm();

      expect(() => {
        component.setStartDate(inputEvent(''));
        fixture.detectChanges();
      }).not.toThrow();

      expect(component.recurrenceStartDateError()).toContain('Choose the date');
      expect(component.canSubmit()).toBe(false);
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('Choose the date');
    });

    it.each(['1e21', '999999999999999999999', 'Infinity'])(
      'renders the interval %s as a message rather than throwing',
      (value) => {
        const { fixture, component } = recurringForm();

        expect(() => {
          component.setIntervalValue(inputEvent(value));
          fixture.detectChanges();
        }).not.toThrow();

        expect(component.recurrenceIntervalError()).toBeDefined();
        expect(component.canSubmit()).toBe(false);
      },
    );

    it('renders an enormous occurrence count as a message rather than throwing', () => {
      const { fixture, component } = recurringForm();

      expect(() => {
        component.setOccurrenceCount(inputEvent(String(Number.MAX_SAFE_INTEGER)));
        fixture.detectChanges();
      }).not.toThrow();

      expect(component.recurrenceOccurrenceCountError()).toBeDefined();
      // The "last on ..." summary has nothing it can honestly compute.
      expect(component.recurrenceEndsLabel()).toBe('—');
    });

    it('survives a cleared end date on the end-date arm', () => {
      const { fixture, component } = recurringForm();

      expect(() => {
        component.setEndCondition('endDate');
        component.setEndDate(inputEvent(''));
        component.setStartDate(inputEvent(''));
        fixture.detectChanges();
      }).not.toThrow();

      expect(component.canSubmit()).toBe(false);
      expect(component.recurrenceEndsLabel()).toBe('—');
    });

    it('sends nothing while any of it is invalid', () => {
      const { component } = recurringForm();

      component.setStartDate(inputEvent(''));
      component.confirmBooking();

      httpMock.expectNone(`${API}/recurrence-rules`);
    });
  });

  // Step 5. The catalogue itself is covered in booking-rejection.spec.ts;
  // these are about what the screen *does* with it — where each message
  // lands, and which actions it offers.
  describe('rendering a rejection', () => {
    function submitAndFail(
      status: number,
      body: string | object,
      detailOverrides: Partial<ResourceDetail> = {},
    ) {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail(detailOverrides));

      component.confirmBooking();
      httpMock.expectOne(`${API}/bookings`).flush(body, { status, statusText: 'Error' });
      fixture.detectChanges();

      const root = fixture.nativeElement as HTMLElement;
      return {
        component,
        root,
        submitError: () => root.querySelector('.submit-error'),
        actionHrefs: () =>
          Array.from(root.querySelectorAll('.submit-error a')).map((a) => a.getAttribute('href')),
      };
    }

    function problemBody(status: number, reasonCode: string, errors?: Record<string, string[]>) {
      return { title: 'Rejected', status, reasonCode, correlationId: 'c1', ...(errors ? { errors } : {}) };
    }

    it('shows a taken slot at the top of the form, with a link back to availability', () => {
      const { submitError, actionHrefs } = submitAndFail(409, problemBody(409, 'SlotUnavailable'));

      expect(submitError()?.textContent).toContain('booked this time while you were filling in the form');
      expect(actionHrefs()).toEqual(['/resources/r1/availability']);
    });

    // Re-checking cannot change a rule refusal, so no action is offered.
    it('offers no action for a rule refusal', () => {
      const { submitError, actionHrefs } = submitAndFail(422, problemBody(422, 'BlackoutPeriod'));

      expect(submitError()?.textContent).toContain('blackout period');
      expect(actionHrefs()).toEqual([]);
    });

    it('puts a duration refusal against the duration control, not the top of the form', () => {
      const { component, submitError, root } = submitAndFail(
        422,
        problemBody(422, 'BookingDurationOutOfRange'),
      );

      expect(component.durationError()).toContain('length is outside what the resource allows');
      expect(submitError()).toBeNull();
      expect(root.querySelector('.field-error')?.textContent).toContain('length is outside');
    });

    it('puts a title validation failure against the title input', () => {
      const { component, submitError } = submitAndFail(
        400,
        problemBody(400, 'ValidationFailed', { Title: ['Too long.'] }),
      );

      expect(component.titleError()).toBe('Too long.');
      expect(submitError()).toBeNull();
    });

    it('puts a quantity validation failure against the stepper', () => {
      const { component } = submitAndFail(
        400,
        problemBody(400, 'ValidationFailed', { Quantity: ['Quantity must be greater than zero.'] }),
        { capacity: 4 },
      );

      expect(component.quantityError()).toBe('Quantity must be greater than zero.');
    });

    // The flagged idempotency gap, as the member sees it: no retry button
    // anywhere, and a link to check whether the booking exists.
    it('offers a way to check, never a retry, when the outcome is unknown', () => {
      const { component, submitError, actionHrefs, root } = submitAndFail(0, new ProgressEvent('error'));

      expect(component.rejection()?.mayHaveBeenCreated).toBe(true);
      expect(submitError()?.textContent).toContain('may have been created');
      // **The date is the point of this assertion**, not just the route. A list
      // had a top, so "go and check" was enough; a calendar does not, so the
      // link has to land on the week the attempted booking is in or it is worse
      // than what it replaced (wp7-plan.md, Phase 4's landing-screen section).
      expect(actionHrefs()).toEqual([`/calendar?view=week&date=${viewerLocalDateOf('2026-09-24T13:15:00Z')}`]);
      expect(root.querySelector('button.confirm-button')?.textContent).not.toContain('Try again');
    });

    it('switches to the not-found state when the resource is gone', () => {
      const { component, root } = submitAndFail(404, problemBody(404, 'ResourceNotFound'));

      expect(component.notFound()).toBe(true);
      expect(root.textContent).toContain("doesn't exist, or you don't have access");
      expect(root.querySelector('.submit-error')).toBeNull();
    });

    it('clears the previous rejection when the form is submitted again', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail());

      component.confirmBooking();
      httpMock
        .expectOne(`${API}/bookings`)
        .flush(problemBody(409, 'SlotUnavailable'), { status: 409, statusText: 'Conflict' });
      expect(component.rejection()).not.toBeNull();

      component.confirmBooking();
      const retry = httpMock.expectOne(`${API}/bookings`);
      expect(component.rejection()).toBeNull();
      retry.flush(
        {
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
        },
        { status: 201, statusText: 'Created' },
      );
    });
  });

  // FR-7.1. Step 4: the two outcomes are told apart at the moment of
  // booking, not left for the member to discover in a list later.
  describe('the outcome panel', () => {
    function submitAndFlush(
      detailOverrides: Partial<ResourceDetail>,
      response: Partial<CreateBookingResponse>,
    ) {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableBookingComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail(detailOverrides));

      component.confirmBooking();
      httpMock.expectOne(`${API}/bookings`).flush(
        {
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
          ...response,
        },
        { status: 201, statusText: 'Created' },
      );
      fixture.detectChanges();
      return { fixture, component, text: () => (fixture.nativeElement as HTMLElement).textContent ?? '' };
    }

    it('states a Confirmed booking plainly, with no approval section', () => {
      const { component, text } = submitAndFlush({ requiresApproval: false, timeZoneId: 'UTC' }, {});

      expect(component.isPending()).toBe(false);
      expect(text()).toContain('Booking confirmed');
      expect(text()).toContain('is reserved for you');
      expect(text()).not.toContain('not held for you yet');
      expect(text()).not.toContain('Waiting on');
    });

    it('shows the reserved span, duration and title from the response', () => {
      const { component, text } = submitAndFlush(
        { timeZoneId: 'UTC' },
        { title: 'Sprint review' },
      );

      expect(component.createdSpan()).toEqual({ date: 'Thu, Sep 24, 2026', timeRange: '13:15 – 15:30' });
      expect(component.createdDurationLabel()).toBe('2 hours 15 minutes');
      expect(text()).toContain('Sprint review');
    });

    // The panel reads the *response*, not the form's own selection: what was
    // reserved is whatever the server says was reserved.
    it('renders the server\'s span even when it differs from what was asked for', () => {
      const { component } = submitAndFlush(
        { timeZoneId: 'UTC' },
        { startsAtUtc: '2026-09-24T08:00:00Z', endsAtUtc: '2026-09-24T09:00:00Z' },
      );

      expect(component.createdSpan()?.timeRange).toBe('08:00 – 09:00');
      expect(component.createdDurationLabel()).toBe('1 hour');
    });

    it('tells a Pending booking apart, names who decides, and says the slot is not held', () => {
      const { component, text } = submitAndFlush(
        {
          requiresApproval: true,
          timeZoneId: 'UTC',
          approvers: [
            { userId: 'u9', fullName: 'Resource Approver' },
            { userId: 'u8', fullName: 'Second Approver' },
          ],
        },
        { status: 'Pending', approval: { approvalRequestId: 'a1', expiresAtUtc: '2026-09-18T09:00:00Z' } },
      );

      expect(component.isPending()).toBe(true);
      expect(text()).toContain('Booking requested');
      expect(text()).toContain('not held for you yet');
      expect(text()).toContain('Waiting on Resource Approver, Second Approver');
      expect(text()).toContain('This request expires on');
      expect(text()).not.toContain('is reserved for you');
    });

    // Decision `0018`: a resource's own approvers decide — but a TenantAdmin
    // can approve anything in the tenant, so "nobody assigned" still has an
    // answer to "who decides".
    it('names a tenant administrator when the resource lists no approvers', () => {
      const { text } = submitAndFlush(
        { requiresApproval: true, approvers: [] },
        { status: 'Pending', approval: { approvalRequestId: 'a1', expiresAtUtc: '2026-09-18T09:00:00Z' } },
      );

      expect(text()).toContain('Waiting on a tenant administrator');
    });

    // FR-7.4: a tenant with no configured ApprovalExpiryHours leaves requests
    // pending indefinitely — a legitimate configuration, not a missing value.
    it('says so when an approval request never expires', () => {
      const { component, text } = submitAndFlush(
        { requiresApproval: true },
        { status: 'Pending', approval: { approvalRequestId: 'a1', expiresAtUtc: null } },
      );

      expect(component.approvalExpiryLabel()).toBeNull();
      expect(text()).toContain("doesn't expire");
      expect(text()).not.toContain('This request expires on');
    });

    it('offers a way onward, and the form is gone', () => {
      const { fixture } = submitAndFlush({ requiresApproval: false }, {});
      const root = fixture.nativeElement as HTMLElement;

      const links = Array.from(root.querySelectorAll('.outcome-actions a')).map((a) => a.getAttribute('href'));
      expect(links).toEqual([
        `/calendar?view=week&date=${viewerLocalDateOf('2026-09-24T13:15:00Z')}`,
        '/resources/r1/availability',
      ]);
      // The form and the "need to make a change?" bar belong to a booking
      // that hasn't happened yet.
      expect(root.querySelector('#booking-title')).toBeNull();
      expect(root.querySelector('.change-bar')).toBeNull();
    });

    it('shows the quantity for a pooled resource', () => {
      const { text } = submitAndFlush({ capacity: 4 }, { quantity: 3 });
      expect(text()).toContain('Quantity');
    });

    // Decision `0005`'s amendment: Capacity 1 admits no quantity but 1, so
    // stating it would be noise.
    it('omits the quantity for an exclusive resource', () => {
      const { text } = submitAndFlush({ capacity: 1 }, { quantity: 1 });
      expect(text()).not.toContain('Quantity');
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
