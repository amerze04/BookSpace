import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { BookingComponent } from './booking.component';
import { BookingSelection } from './booking-arrival';
import { CreateBookingResponse } from './booking.models';
import { BookingRejection } from './booking-rejection';
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
      expect(actionHrefs()).toEqual(['/my-bookings']);
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
      expect(links).toEqual(['/my-bookings', '/resources/r1/availability']);
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
