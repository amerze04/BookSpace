import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AvailabilityComponent } from './availability.component';
import { BreadcrumbService } from '../../layout/breadcrumb.service';
import { ResourceDetail } from '../resources/resources.models';
import { AvailabilityResponse } from './availability.models';
import { DayRow, DaySegment } from './availability-grid';

const API = 'http://localhost:5270';

// The signals/methods this spec drives are `protected` at compile time only
// — same widened-type pattern every other component spec in this app uses.
type TestableAvailabilityComponent = AvailabilityComponent & {
  resource: () => ResourceDetail | null;
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  retry(): void;
  capacityLabel(resource: ResourceDetail): string;
  approvalLabel(resource: ResourceDetail): string;
  fromDate: () => string;
  toDate: () => string;
  quantity: () => number;
  formattedRange: () => string;
  showQuantityStepper: () => boolean;
  showRangePicker: () => boolean;
  draftFrom: () => string;
  draftTo: () => string;
  draftError: () => string | null;
  goToToday(): void;
  shiftWeek(direction: 1 | -1): void;
  toggleRangePicker(): void;
  cancelRangePicker(): void;
  applyRangePicker(): void;
  onDraftFromChange(event: Event): void;
  onDraftToChange(event: Event): void;
  onDocumentClick(event: MouseEvent): void;
  incrementQuantity(): void;
  decrementQuantity(): void;
  availabilityResponse: () => AvailabilityResponse | null;
  availabilityLoading: () => boolean;
  availabilityError: () => boolean;
  isArchivedResource: () => boolean;
  dayRows: () => DayRow[];
  retryAvailability(): void;
  segmentLabel(segment: DaySegment): string;
  emptyDayLabel(date: string): string;
  segmentAccessibleLabel(date: string, segment: DaySegment): string;
  selectedSegment: () => DaySegment | null;
  selectedStartMinutes: () => number;
  // .set is exposed on the End signal so the off-grid regression below can
  // put it where a drag would, without simulating a whole pointer sequence.
  selectedEndMinutes: { (): number; set(minutes: number): void };
  startTimeOptions: () => { minutes: number; label: string }[];
  endTimeOptions: () => { minutes: number; label: string }[];
  selectedDateLabel: () => string;
  selectedTimeRangeLabel: () => string;
  selectedDurationLabel: () => string;
  isSegmentSelected(segment: DaySegment): boolean;
  selectSegment(segment: DaySegment): void;
  clearSelection(): void;
  onSelectedStartChange(event: Event): void;
  onSelectedEndChange(event: Event): void;
  continueToBooking(resource: ResourceDetail): void;
  durationError: () => string | null;
  selectionOverlayLeftPercent(): number;
  selectionOverlayWidthPercent(): number;
  onHandlePointerDown(edge: 'start' | 'end', event: PointerEvent): void;
  onHandlePointerMove(edge: 'start' | 'end', event: PointerEvent): void;
  onHandlePointerUp(): void;
  onOverlayPointerDown(event: PointerEvent): void;
  onOverlayPointerMove(event: PointerEvent): void;
};

// A minimal fake handle element inside a fake .day-track, sized so the drag
// tests can reason about pixel-to-minute conversion with round numbers: 600px
// wide standing in for whatever the axis currently spans.
function fakeHandleInTrack(trackWidthPx = 600): HTMLElement {
  const track = document.createElement('div');
  track.className = 'day-track';
  track.getBoundingClientRect = () =>
    ({ left: 0, right: trackWidthPx, width: trackWidthPx, top: 0, bottom: 0, height: 0, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
  const handle = document.createElement('span');
  handle.className = 'selection-handle';
  handle.setPointerCapture = vi.fn();
  track.appendChild(handle);
  return handle;
}

// clientX defaults to 0 — the origin a move-drag test's own delta is
// relative to; stopPropagation/preventDefault are both real handlers rely on
// (stopPropagation to keep a handle's pointerdown from also triggering the
// overlay's own 'move' drag underneath it).
function fakePointerDownEvent(handle: HTMLElement, clientX = 0): PointerEvent {
  return {
    currentTarget: handle,
    pointerId: 1,
    clientX,
    preventDefault: () => {},
    stopPropagation: () => {},
  } as unknown as PointerEvent;
}

function fakePointerMoveEvent(clientX: number): PointerEvent {
  return { clientX } as unknown as PointerEvent;
}

function inputChangeEvent(value: string): Event {
  const input = document.createElement('input');
  input.value = value;
  return { target: input } as unknown as Event;
}

function clickEventWithTarget(target: HTMLElement): MouseEvent {
  return { target } as unknown as MouseEvent;
}

function fakeDetail(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: 'Main conference room',
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'Europe/Sarajevo',
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

// A generic empty response — enough to satisfy the availability request
// every successful resource load now cascades into (step 5), for tests that
// only care about the resource-load side of things and would otherwise fail
// httpMock.verify() over an unflushed request they never meant to exercise.
function fakeAvailability(overrides: Partial<AvailabilityResponse> = {}): AvailabilityResponse {
  return {
    resourceId: 'r1',
    timeZoneId: 'UTC',
    fromLocalDate: '2026-09-21',
    toLocalDate: '2026-09-27',
    quantity: 1,
    isArchived: false,
    intervals: [],
    ...overrides,
  };
}

describe('AvailabilityComponent', () => {
  let httpMock: HttpTestingController;
  let breadcrumbService: BreadcrumbService;
  let paramMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let routerNavigate: ReturnType<typeof vi.fn>;

  // A BehaviorSubject, not a bare `of(...)`, mirroring ResourceDetailComponent's
  // own spec — this screen is reachable directly (a bare URL, a refresh), so
  // it has to load itself from the route param alone, and re-fetch if that
  // param ever changes under it without the component being recreated.
  function createFixture(id = 'r1') {
    paramMap$ = new BehaviorSubject(convertToParamMap({ id }));
    routerNavigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [AvailabilityComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // RouterLink ("View details" / "Back to Resources") injects both,
        // whether or not a test ever calls detectChanges() — navigate is a
        // spy so "Continue to booking" (step 6) can be asserted on directly.
        { provide: Router, useValue: { navigate: routerNavigate } },
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
    return TestBed.createComponent(AvailabilityComponent);
  }

  // Flushes the availability request a successful resource load cascades
  // into (step 5) — matched by URL only (no query-param assertion), since
  // most callers here don't care what exact from/to/quantity landed. The
  // "date-range navigation and quantity" and "availability grid" describe
  // blocks below assert those specifically where it matters.
  function flushAvailability(id: string, overrides: Partial<AvailabilityResponse> = {}): void {
    httpMock
      .expectOne((r) => r.url === `${API}/resources/${id}/availability`)
      .flush(fakeAvailability({ resourceId: id, ...overrides }));
  }

  // Flushes both requests a successful resource load now triggers.
  function flushResource(
    id: string,
    detailOverrides: Partial<ResourceDetail> = {},
    availabilityOverrides: Partial<AvailabilityResponse> = {},
  ): void {
    httpMock.expectOne(`${API}/resources/${id}`).flush(fakeDetail({ id, ...detailOverrides }));
    flushAvailability(id, availabilityOverrides);
  }

  afterEach(() => {
    httpMock.verify();
    breadcrumbService.setInsertBeforeLast(null);
  });

  it('fetches the resource named by the route id', () => {
    createFixture('r1');

    const req = httpMock.expectOne(`${API}/resources/r1`);
    expect(req.request.method).toBe('GET');
    req.flush(fakeDetail());
    flushAvailability('r1');
  });

  it('sets the resource on success and inserts its name into the breadcrumb', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableAvailabilityComponent;

    flushResource('r1', { name: 'Conference Room A' });

    expect(component.resource()?.name).toBe('Conference Room A');
    expect(component.loading()).toBe(false);
    // insertBeforeLast, not override — see BreadcrumbService's own comment
    // on why this screen can't simply replace the last crumb.
    expect(breadcrumbService.insertBeforeLast()).toBe('Conference Room A');
    expect(breadcrumbService.override()).toBeNull();
  });

  it('clears the inserted breadcrumb crumb once the component is destroyed', () => {
    const fixture = createFixture();
    flushResource('r1', { name: 'Conference Room A' });
    expect(breadcrumbService.insertBeforeLast()).toBe('Conference Room A');

    fixture.destroy();

    expect(breadcrumbService.insertBeforeLast()).toBeNull();
  });

  it('shows a not-found state on a 404, distinct from a generic load error', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableAvailabilityComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
    expect(component.loadError()).toBe(false);
  });

  it('shows a generic error and recovers via retry() on anything else', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableAvailabilityComponent;

    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);
    expect(component.notFound()).toBe(false);

    component.retry();
    flushResource('r1');

    expect(component.loadError()).toBe(false);
    expect(component.resource()).not.toBeNull();
  });

  it('re-fetches when the route param changes without the component being recreated', () => {
    const fixture = createFixture('r1');
    flushResource('r1', { name: 'Conference Room A' });

    paramMap$.next(convertToParamMap({ id: 'r2' }));

    flushResource('r2', { name: 'Pool Cars' });

    const component = fixture.componentInstance as TestableAvailabilityComponent;
    expect(component.resource()?.name).toBe('Pool Cars');
    expect(breadcrumbService.insertBeforeLast()).toBe('Pool Cars');
  });

  // Item 4: unlike the availability-fetch stage (already guarded by
  // latestAvailabilityRequestId, tested separately below), the resource-load
  // stage itself previously had no staleness guard at all — A starts
  // loading -> route changes to B -> B completes -> A completes later would
  // let stale A overwrite B. switchMap makes that impossible by construction
  // (A's in-flight request is cancelled the moment B's id comes through),
  // and B's own availability fetch must use B's metadata, not anything left
  // over from A.
  it('cancels a still-pending resource fetch when the route id changes before it resolves, and the new resource\'s own availability fetch uses its own metadata', () => {
    const fixture = createFixture('r1');
    const component = fixture.componentInstance as TestableAvailabilityComponent;
    const firstReq = httpMock.expectOne(`${API}/resources/r1`);

    paramMap$.next(convertToParamMap({ id: 'r2' }));

    const secondReq = httpMock.expectOne(`${API}/resources/r2`);
    secondReq.flush(fakeDetail({ id: 'r2', name: 'Pool Cars', timeZoneId: 'UTC', capacity: 5 }));

    expect(firstReq.cancelled).toBe(true);
    expect(component.resource()?.name).toBe('Pool Cars');
    expect(component.loading()).toBe(false);

    // The availability fetch this cascades into is for r2, not r1.
    const availabilityReq = httpMock.expectOne((r) => r.url === `${API}/resources/r2/availability`);
    availabilityReq.flush(fakeAvailability({ resourceId: 'r2', timeZoneId: 'UTC' }));

    expect(component.availabilityResponse()?.resourceId).toBe('r2');
  });

  it('labels an exclusive resource "Single resource" and a pooled one by its unit count', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableAvailabilityComponent;
    flushResource('r1');

    expect(component.capacityLabel(fakeDetail({ capacity: 1 }))).toBe('Single resource');
    expect(component.capacityLabel(fakeDetail({ capacity: 5 }))).toBe('5 units');
  });

  it('labels approval required vs. not required, unconditionally (unlike the list card\'s badge)', () => {
    const fixture = createFixture();
    const component = fixture.componentInstance as TestableAvailabilityComponent;
    flushResource('r1');

    expect(component.approvalLabel(fakeDetail({ requiresApproval: true }))).toBe('Approval required');
    expect(component.approvalLabel(fakeDetail({ requiresApproval: false }))).toBe('No approval required');
  });

  describe('date-range navigation and quantity (step 4)', () => {
    // A fixed instant, resource in UTC, so "today in the resource's own
    // timezone" is exactly the calendar date in the assertions below rather
    // than something that has to be re-derived per test.
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-09-21T10:00:00Z'));
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    it('initializes a 7-day window starting today in the resource\'s own timezone on first load', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      expect(component.fromDate()).toBe('2026-09-21');
      expect(component.toDate()).toBe('2026-09-27');
      expect(component.formattedRange()).toBe('Sep 21, 2026 – Sep 27, 2026');
    });

    it('shiftWeek moves the window by 7 days in either direction', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.shiftWeek(1);
      flushAvailability('r1'); // each range change fires its own request
      expect(component.fromDate()).toBe('2026-09-28');
      expect(component.toDate()).toBe('2026-10-04');

      component.shiftWeek(-1);
      flushAvailability('r1');
      component.shiftWeek(-1);
      flushAvailability('r1');
      expect(component.fromDate()).toBe('2026-09-14');
      expect(component.toDate()).toBe('2026-09-20');
    });

    it('goToToday resets to a fresh 7-day window regardless of where shiftWeek left off', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.shiftWeek(1);
      flushAvailability('r1');
      component.shiftWeek(1);
      flushAvailability('r1');
      component.goToToday();
      flushAvailability('r1');

      expect(component.fromDate()).toBe('2026-09-21');
      expect(component.toDate()).toBe('2026-09-27');
    });

    it('resets the range and quantity when the route param switches to a different resource', () => {
      const fixture = createFixture('r1');
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC', capacity: 5 });
      component.incrementQuantity();
      flushAvailability('r1'); // each quantity click fires its own request
      component.incrementQuantity();
      flushAvailability('r1');
      expect(component.quantity()).toBe(3);

      paramMap$.next(convertToParamMap({ id: 'r2' }));
      flushResource('r2', { timeZoneId: 'UTC', capacity: 5 });

      // Same "today" window recomputed for the new resource, not the stale
      // value carried over, and quantity back to the honest default of 1.
      expect(component.fromDate()).toBe('2026-09-21');
      expect(component.toDate()).toBe('2026-09-27');
      expect(component.quantity()).toBe(1);
    });

    it('hides the quantity stepper for an exclusive resource and shows it for a pooled one', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { capacity: 1 });

      expect(component.showQuantityStepper()).toBe(false);
    });

    it('shows the quantity stepper for a pooled resource', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { capacity: 5 });

      expect(component.showQuantityStepper()).toBe(true);
    });

    it('clamps the quantity stepper between 1 and the resource\'s capacity', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { capacity: 3 });

      component.decrementQuantity();
      flushAvailability('r1'); // even a no-op clamp (already at 1) still fires
      expect(component.quantity()).toBe(1); // floor, never below 1

      component.incrementQuantity();
      flushAvailability('r1');
      component.incrementQuantity();
      flushAvailability('r1');
      component.incrementQuantity();
      flushAvailability('r1');
      component.incrementQuantity();
      flushAvailability('r1');
      expect(component.quantity()).toBe(3); // ceiling, never above capacity
    });

    it('resets quantity to 1 on a reload if it would now exceed the resource\'s capacity', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { capacity: 5 });
      component.incrementQuantity();
      flushAvailability('r1');
      component.incrementQuantity();
      flushAvailability('r1');
      expect(component.quantity()).toBe(3);

      // A retry() that happens to come back with a smaller capacity than
      // before (the resource was edited elsewhere) — the clamp on load
      // catches this even without a route-param change.
      component.retry();
      flushResource('r1', { capacity: 2 });

      expect(component.quantity()).toBe(1);
    });

    it('opens the range picker with the current range as its draft', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();

      expect(component.showRangePicker()).toBe(true);
      expect(component.draftFrom()).toBe('2026-09-21');
      expect(component.draftTo()).toBe('2026-09-27');
      expect(component.draftError()).toBeNull();
    });

    it('applies a valid custom range and closes the picker', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();
      component.onDraftFromChange(inputChangeEvent('2026-10-01'));
      component.onDraftToChange(inputChangeEvent('2026-10-05'));
      component.applyRangePicker();
      flushAvailability('r1');

      expect(component.showRangePicker()).toBe(false);
      expect(component.fromDate()).toBe('2026-10-01');
      expect(component.toDate()).toBe('2026-10-05');
    });

    it('rejects an inverted range without closing the picker or changing the committed range', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();
      component.onDraftFromChange(inputChangeEvent('2026-10-05'));
      component.onDraftToChange(inputChangeEvent('2026-10-01'));
      component.applyRangePicker();

      expect(component.showRangePicker()).toBe(true);
      expect(component.draftError()).toBe('The end date must not be before the start date.');
      expect(component.fromDate()).toBe('2026-09-21'); // unchanged
    });

    it('rejects a range over the 90-day cap client-side, mirroring the backend\'s own limit', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();
      component.onDraftFromChange(inputChangeEvent('2026-01-01'));
      component.onDraftToChange(inputChangeEvent('2026-04-15')); // 105 days
      component.applyRangePicker();

      expect(component.showRangePicker()).toBe(true);
      expect(component.draftError()).toBe('The range must not exceed 90 days.');
    });

    it('cancelRangePicker closes without touching the committed range', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();
      component.onDraftFromChange(inputChangeEvent('2026-11-01'));
      component.cancelRangePicker();

      expect(component.showRangePicker()).toBe(false);
      expect(component.fromDate()).toBe('2026-09-21');
      expect(component.toDate()).toBe('2026-09-27');
    });

    it('re-clicking the range display closes the picker instead of reopening it on top of itself', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();
      expect(component.showRangePicker()).toBe(true);

      component.toggleRangePicker();
      expect(component.showRangePicker()).toBe(false);
      // Same discard-the-draft behavior as Cancel, not Apply.
      expect(component.fromDate()).toBe('2026-09-21');
      expect(component.toDate()).toBe('2026-09-27');
    });

    it('a click outside the range-picker-wrapper closes the picker', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();
      expect(component.showRangePicker()).toBe(true);

      const elsewhere = document.createElement('button');
      component.onDocumentClick(clickEventWithTarget(elsewhere));

      expect(component.showRangePicker()).toBe(false);
    });

    it('a click inside the range-picker-wrapper (e.g. a date field) leaves the picker open', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      component.toggleRangePicker();

      const wrapper = document.createElement('div');
      wrapper.className = 'range-picker-wrapper';
      const dateInput = document.createElement('input');
      wrapper.appendChild(dateInput);

      component.onDocumentClick(clickEventWithTarget(dateInput));

      expect(component.showRangePicker()).toBe(true);
    });

    it('a click anywhere does nothing while the picker is already closed', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      flushResource('r1', { timeZoneId: 'UTC' });

      expect(component.showRangePicker()).toBe(false);
      component.onDocumentClick(clickEventWithTarget(document.createElement('button')));

      expect(component.showRangePicker()).toBe(false);
    });
  });

  describe('availability grid (step 5)', () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-09-21T10:00:00Z'));
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    it('fetches availability for the initialized range and quantity once the resource loads', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));

      const req = httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`);
      expect(req.request.params.get('from')).toBe('2026-09-21');
      expect(req.request.params.get('to')).toBe('2026-09-27');
      expect(req.request.params.get('quantity')).toBe('1');

      req.flush(fakeAvailability());
      expect(component.availabilityResponse()).not.toBeNull();
      expect(component.availabilityLoading()).toBe(false);
    });

    it('builds one day row per date in range, assigning each interval to its own local day', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [
            { startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T12:00:00Z', remainingCapacity: 1 },
            { startUtc: '2026-09-23T09:00:00Z', endUtc: '2026-09-23T15:00:00Z', remainingCapacity: 1 },
          ],
        }),
      );

      const rows = component.dayRows();
      expect(rows).toHaveLength(7); // Sep 21-27 inclusive
      expect(rows[0].date).toBe('2026-09-21');
      expect(rows[0].segments).toHaveLength(1);
      expect(rows[1].date).toBe('2026-09-22');
      expect(rows[1].segments).toHaveLength(0); // "No bookable hours" day
      expect(rows[2].segments).toHaveLength(1);
    });

    it('gives an archived resource its own state, distinct from every day simply being closed', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', isArchived: true }));
      httpMock
        .expectOne((r) => r.url === `${API}/resources/r1/availability`)
        .flush(fakeAvailability({ isArchived: true, intervals: [] }));

      expect(component.isArchivedResource()).toBe(true);
    });

    it('does not confuse an archived response with an ordinary empty-but-open range', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      httpMock
        .expectOne((r) => r.url === `${API}/resources/r1/availability`)
        .flush(fakeAvailability({ isArchived: false, intervals: [] }));

      expect(component.isArchivedResource()).toBe(false);
      expect(component.dayRows().every((row) => row.segments.length === 0)).toBe(true);
    });

    it('shows an availability load error and recovers via retryAvailability()', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      httpMock
        .expectOne((r) => r.url === `${API}/resources/r1/availability`)
        .flush(null, { status: 500, statusText: 'Server Error' });

      expect(component.availabilityError()).toBe(true);
      expect(component.availabilityLoading()).toBe(false);

      component.retryAvailability();
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability());

      expect(component.availabilityError()).toBe(false);
      expect(component.availabilityResponse()).not.toBeNull();
    });

    it('ignores a stale availability response that resolves after a newer request already landed', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      const firstReq = httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`);

      // A second request goes out (shiftWeek) before the first ever comes
      // back — the same race ResourceListComponent's own latestRequestId
      // guard already covers for its filters.
      component.shiftWeek(1);
      const secondReq = httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`);

      secondReq.flush(fakeAvailability({ fromLocalDate: '2026-09-28', toLocalDate: '2026-10-04' }));
      expect(component.availabilityResponse()?.fromLocalDate).toBe('2026-09-28');

      firstReq.flush(fakeAvailability({ fromLocalDate: '2026-09-21', toLocalDate: '2026-09-27' }));
      // The stale, older response must not clobber the newer one that
      // already landed.
      expect(component.availabilityResponse()?.fromLocalDate).toBe('2026-09-28');
    });

    it('labels a segment with its time range on an exclusive resource', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity: 1 }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability());

      expect(
        component.segmentLabel({
          date: '2026-09-21',
          startMinutes: 8 * 60,
          endMinutes: 12 * 60,
          startUtc: '2026-09-21T08:00:00.000Z',
          endUtc: '2026-09-21T12:00:00.000Z',
          remainingCapacity: 1,
        }),
      ).toBe('08:00 – 12:00');
    });

    it('labels a segment with "N left" on a pooled resource, matching the design\'s bar text', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity: 5 }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability({ quantity: 3 }));

      expect(
        component.segmentLabel({
          date: '2026-09-21',
          startMinutes: 8 * 60,
          endMinutes: 12 * 60,
          startUtc: '2026-09-21T08:00:00.000Z',
          endUtc: '2026-09-21T12:00:00.000Z',
          remainingCapacity: 3,
        }),
      ).toBe('3 left');
    });

    // Item 6: the API returns bookable intervals only — an empty day can
    // mean fully booked, blacked out, insufficient pooled capacity, or a
    // genuinely closed weekday, and the API deliberately doesn't say which.
    // "No bookable hours" is only used when the resource's own
    // availabilityWindows prove the weekday has no window at all; every
    // other empty day gets the honest, non-specific "No availability".
    it('emptyDayLabel distinguishes a proven-closed weekday from a merely-empty one on the same response', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(
        fakeDetail({
          timeZoneId: 'UTC',
          // Only Monday has a window — Tuesday has none at all.
          availabilityWindows: [{ id: 'w1', weekday: 'Monday', opensAt: '08:00:00', closesAt: '17:00:00' }],
        }),
      );
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability({ intervals: [] }));

      // 2026-09-21 is a Monday (has a window, so an empty day here means
      // fully booked/blacked out, not closed) and 2026-09-22 is a Tuesday
      // (no window at all — genuinely closed).
      expect(component.emptyDayLabel('2026-09-21')).toBe('No availability');
      expect(component.emptyDayLabel('2026-09-22')).toBe('No bookable hours');
    });

    // Item 10: the segment button's own visible text is short ("3 left",
    // or just the time range) — this is what a screen reader announces
    // instead, spelling out the day and full time range.
    it('segmentAccessibleLabel spells out the day and time range, with "to" rather than an en dash', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity: 1 }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability());

      const label = component.segmentAccessibleLabel('2026-09-22', {
        date: '2026-09-22',
        startMinutes: 10 * 60,
        endMinutes: 12 * 60,
        startUtc: '2026-09-22T10:00:00.000Z',
        endUtc: '2026-09-22T12:00:00.000Z',
        remainingCapacity: 1,
      });

      expect(label).toBe('Tuesday, Sep 22, 10:00 to 12:00');
    });

    it('segmentAccessibleLabel appends the remaining-units count for a pooled resource', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity: 5 }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability({ quantity: 1 }));

      const label = component.segmentAccessibleLabel('2026-09-22', {
        date: '2026-09-22',
        startMinutes: 10 * 60,
        endMinutes: 12 * 60,
        startUtc: '2026-09-22T10:00:00.000Z',
        endUtc: '2026-09-22T12:00:00.000Z',
        remainingCapacity: 3,
      });

      expect(label).toBe('Tuesday, Sep 22, 10:00 to 12:00, 3 units remaining');
    });

    it('renders aria-pressed and the accessible label on the actual segment button, and flips aria-pressed on selection', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity: 1 }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T12:00:00Z', remainingCapacity: 1 }],
        }),
      );
      fixture.detectChanges();

      const button = (fixture.nativeElement as HTMLElement).querySelector('button.segment');
      expect(button?.getAttribute('aria-pressed')).toBe('false');
      expect(button?.getAttribute('aria-label')).toBe('Monday, Sep 21, 08:00 to 12:00');

      component.selectSegment(component.dayRows()[0].segments[0]);
      fixture.detectChanges();

      expect(button?.getAttribute('aria-pressed')).toBe('true');
    });
  });

  describe('selection (step 6)', () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-09-21T10:00:00Z'));
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    // A single bookable segment, 08:00-17:00 on 2026-09-21, on a resource in
    // UTC (so local minutes-of-day match the UTC hour directly, keeping the
    // arithmetic in each assertion easy to check by eye).
    function loadWithOneSegment(component: TestableAvailabilityComponent, capacity = 1): void {
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', capacity }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: capacity }],
        }),
      );
    }

    // Bug found by the owner, 2026-09-17, on the seeded 3D Printer (min 60,
    // max 180): clicking a bar selected 09:00-12:00, but the End dropdown
    // displayed 10:00 — its own *first* option, Start + the minimum — and
    // every subsequent Start change moved that displayed number without
    // moving the selection. Cause: `[value]` on the <select> sets the value
    // property once, while the browser resets a single select to its first
    // option whenever the option list is rebuilt (endTimeOptions depends on
    // selectedStartMinutes, so it is rebuilt constantly) and Angular doesn't
    // re-apply a binding whose own value hasn't changed.
    //
    // These assert against the rendered DOM on purpose: every signal-level
    // assertion in this file was already passing while the screen was wrong.
    describe('the Start/End dropdowns show what is actually selected', () => {
      function loadPrinterLikeResource(component: TestableAvailabilityComponent): void {
        httpMock
          .expectOne(`${API}/resources/r1`)
          .flush(fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: 60, maxDurationMinutes: 180 }));
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);
      }

      function timeSelects(fixture: { nativeElement: unknown }): HTMLSelectElement[] {
        return Array.from(
          (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLSelectElement>('.time-field select'),
        );
      }

      it('renders the defaulted End (Start + the maximum), not the first option', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        loadPrinterLikeResource(component);
        fixture.detectChanges();

        const [startSelect, endSelect] = timeSelects(fixture);
        expect(component.selectedEndMinutes()).toBe(11 * 60); // 08:00 + 180
        expect(startSelect.value).toBe(String(8 * 60));
        expect(endSelect.value).toBe(String(11 * 60)); // not 09:00, the earliest legal End
      });

      it('keeps showing the held End after a Start change rebuilds the option list', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        loadPrinterLikeResource(component);
        fixture.detectChanges();

        // 09:00 start, End 11:00 is still valid (2 hours, inside 1-3), so the
        // selection must not move — and neither must what the dropdown shows.
        component.onSelectedStartChange(inputChangeEvent(String(9 * 60)));
        fixture.detectChanges();

        const [startSelect, endSelect] = timeSelects(fixture);
        expect(component.selectedStartMinutes()).toBe(9 * 60);
        expect(component.selectedEndMinutes()).toBe(11 * 60);
        expect(startSelect.value).toBe(String(9 * 60));
        expect(endSelect.value).toBe(String(11 * 60));
      });

      // The option grid steps from the segment's own start; a drag snaps to
      // absolute 15-minute marks. On a segment starting at an odd offset (a
      // blackout can leave one), the two grids don't line up, so the held
      // value has to be added to the list or the select cannot show it.
      it('offers the held value even when it falls between two steps', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock
          .expectOne(`${API}/resources/r1`)
          .flush(fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: 60, maxDurationMinutes: 180 }));
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:07:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);

        // Where a drag would land: an absolute 11:00, off the 09:07/09:22/…
        // grid the End options otherwise step through.
        component.selectedEndMinutes.set(11 * 60);
        fixture.detectChanges();

        expect(component.endTimeOptions().some((o) => o.minutes === 11 * 60)).toBe(true);
        expect(timeSelects(fixture)[1].value).toBe(String(11 * 60));
      });
    });

    it('selecting a bar defaults Start to the segment\'s own start and End to Start + the resource\'s maxDurationMinutes (owner\'s correction)', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component); // fakeDetail's default maxDurationMinutes: 240

      const segment = component.dayRows()[0].segments[0];
      component.selectSegment(segment);

      expect(component.selectedSegment()).toBe(segment);
      expect(component.selectedStartMinutes()).toBe(8 * 60);
      expect(component.selectedEndMinutes()).toBe(8 * 60 + 240); // 12:00, not the segment's own 17:00
      expect(component.isSegmentSelected(segment)).toBe(true);
      // The default is never itself invalid, unlike selecting the whole
      // 9-hour segment would be against a 4-hour max.
      expect(component.durationError()).toBeNull();
    });

    it('defaults to the whole segment when the resource sets no maxDurationMinutes', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', maxDurationMinutes: null }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
        }),
      );

      component.selectSegment(component.dayRows()[0].segments[0]);

      expect(component.selectedStartMinutes()).toBe(8 * 60);
      expect(component.selectedEndMinutes()).toBe(17 * 60);
    });

    it('formats the selected-time summary line to match the design', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component); // fakeDetail's default maxDurationMinutes: 240

      component.selectSegment(component.dayRows()[0].segments[0]);

      expect(component.selectedDateLabel()).toBe('Mon, Sep 21, 2026');
      expect(component.selectedTimeRangeLabel()).toBe('08:00–12:00');
      expect(component.selectedDurationLabel()).toBe('4 hours');
    });

    it('formats a sub-hour and mixed duration correctly', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component);

      component.selectSegment(component.dayRows()[0].segments[0]);
      component.onSelectedEndChange(inputChangeEvent(String(8 * 60 + 45))); // 08:45, 45-min booking

      expect(component.selectedDurationLabel()).toBe('45 minutes');

      component.onSelectedEndChange(inputChangeEvent(String(10 * 60 + 15))); // 10:15, 2h15m
      expect(component.selectedDurationLabel()).toBe('2 hours 15 minutes');
    });

    it('clearSelection resets everything', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component);

      component.selectSegment(component.dayRows()[0].segments[0]);
      component.clearSelection();

      expect(component.selectedSegment()).toBeNull();
      expect(component.selectedStartMinutes()).toBe(0);
      expect(component.selectedEndMinutes()).toBe(0);
    });

    it('changing the range clears any existing selection, since its segment object goes stale', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component);
      component.selectSegment(component.dayRows()[0].segments[0]);
      expect(component.selectedSegment()).not.toBeNull();

      component.shiftWeek(1);
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability());

      expect(component.selectedSegment()).toBeNull();
    });

    it('startTimeOptions spans the segment in 15-minute steps, always including the exact start', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component); // fakeDetail's default minDurationMinutes: 30

      component.selectSegment(component.dayRows()[0].segments[0]);
      const options = component.startTimeOptions();

      expect(options[0]).toEqual({ minutes: 8 * 60, label: '08:00' });
      expect(options[1]).toEqual({ minutes: 8 * 60 + 15, label: '08:15' });
      // Latest legal start still leaves room for the resource's own 30-minute minimum.
      expect(options[options.length - 1].minutes).toBe(17 * 60 - 30);
    });

    it('endTimeOptions always includes the segment\'s own exact end, even off-step, when nothing caps duration', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(
        fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: 30, maxDurationMinutes: null }),
      );
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
        }),
      );

      component.selectSegment(component.dayRows()[0].segments[0]);
      const options = component.endTimeOptions();

      // Earliest legal End is Start + the resource's 30-minute minimum, not just one step.
      expect(options[0]).toEqual({ minutes: 8 * 60 + 30, label: '08:30' });
      expect(options[options.length - 1]).toEqual({ minutes: 17 * 60, label: '17:00' });
    });

    it('endTimeOptions respects a resource maxDurationMinutes, capping how far End can reach', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(
        fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: 30, maxDurationMinutes: 90 }),
      );
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
        }),
      );

      component.selectSegment(component.dayRows()[0].segments[0]); // Start defaults to 08:00
      const options = component.endTimeOptions();

      // Start (08:00) + maxDuration (90 min) = 09:30 — never 17:00, even
      // though the segment itself runs that far.
      expect(options[options.length - 1]).toEqual({ minutes: 9 * 60 + 30, label: '09:30' });
    });

    it('startTimeOptions falls back to a bare one-step minimum when the resource sets no minDurationMinutes', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
        }),
      );

      component.selectSegment(component.dayRows()[0].segments[0]);
      const options = component.startTimeOptions();

      expect(options[options.length - 1].minutes).toBe(17 * 60 - 15);
    });

    it('durationError flags a segment too short for the resource\'s own minimum, and blocks Continue to booking', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: 60 }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          // Only a 20-minute gap — shorter than the resource's own 60-minute minimum.
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T08:20:00Z', remainingCapacity: 1 }],
        }),
      );

      component.selectSegment(component.dayRows()[0].segments[0]);

      expect(component.durationError()).toContain('at least 1 hour');
      component.continueToBooking(fakeDetail({ id: 'r1' }));
      expect(routerNavigate).not.toHaveBeenCalled();
    });

    // Item 5: minDurationMinutes: null means no configured minimum at all —
    // the resource is valid at whole-second precision on the backend. A
    // segment shorter than the UI's own 15-minute dropdown/drag granularity
    // must never be flagged as violating a "requires at least 15 minutes"
    // rule the resource doesn't actually have, and Continue to booking must
    // not be blocked by one.
    it('durationError never invents a 15-minute minimum for a resource with minDurationMinutes: null', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          // A 10-minute gap — shorter than TIME_OPTION_STEP_MINUTES (15),
          // but there's no real minimum for it to violate.
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T08:10:00Z', remainingCapacity: 1 }],
        }),
      );

      component.selectSegment(component.dayRows()[0].segments[0]);

      expect(component.durationError()).toBeNull();
      component.continueToBooking(fakeDetail({ id: 'r1', timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }));
      expect(routerNavigate).toHaveBeenCalledWith(
        ['/resources', 'r1', 'book'],
        { queryParams: { startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T08:10:00Z', quantity: '1' } },
      );
    });

    it('durationError is null for an ordinary, valid selection', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component); // fakeDetail's default: minDuration 30, maxDuration 240

      component.selectSegment(component.dayRows()[0].segments[0]);
      // The default (Start + maxDuration) is already valid on its own now —
      // narrow further to a 2-hour selection to prove an adjusted, not just
      // the default, selection stays valid too.
      component.onSelectedEndChange(inputChangeEvent(String(10 * 60)));

      expect(component.durationError()).toBeNull();
    });

    it('onSelectedStartChange bumps End forward when it would otherwise be closer than the minimum duration', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component); // fakeDetail's default minDurationMinutes: 30

      component.selectSegment(component.dayRows()[0].segments[0]);
      component.onSelectedEndChange(inputChangeEvent(String(9 * 60))); // End = 09:00
      component.onSelectedStartChange(inputChangeEvent(String(9 * 60))); // Start moves to 09:00, same as End

      expect(component.selectedStartMinutes()).toBe(9 * 60);
      expect(component.selectedEndMinutes()).toBe(9 * 60 + 30); // bumped forward by the 30-min minimum
    });

    it('onSelectedStartChange leaves a still-valid End alone', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component);

      component.selectSegment(component.dayRows()[0].segments[0]);
      component.onSelectedEndChange(inputChangeEvent(String(12 * 60)));
      component.onSelectedStartChange(inputChangeEvent(String(9 * 60)));

      expect(component.selectedEndMinutes()).toBe(12 * 60); // untouched — still after the new Start
    });

    // Query parameters, not router state (owner's decision, 2026-09-17): the
    // selected slot is part of the booking screen's URL, so it can be shared,
    // bookmarked and reopened. The instants are whole-second and UTC —
    // buildBookingQueryParams drops the ".000" toISOString() produces, since
    // the API refuses fractional seconds.
    it('continueToBooking navigates to the book route carrying the selected UTC span and quantity', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component, 5);

      const segment = component.dayRows()[0].segments[0];
      component.selectSegment(segment);
      component.onSelectedStartChange(inputChangeEvent(String(9 * 60)));
      component.onSelectedEndChange(inputChangeEvent(String(11 * 60)));

      // Matches what loadWithOneSegment actually loaded (timeZoneId: 'UTC')
      // — the real template only ever calls this with the current resource()
      // signal's own value, never an independently-built fixture.
      component.continueToBooking(fakeDetail({ id: 'r1', capacity: 5, timeZoneId: 'UTC' }));

      expect(routerNavigate).toHaveBeenCalledWith(
        ['/resources', 'r1', 'book'],
        {
          queryParams: {
            startUtc: '2026-09-21T09:00:00Z',
            endUtc: '2026-09-21T11:00:00Z',
            quantity: '1',
          },
        },
      );
    });

    it('continueToBooking does nothing when nothing is selected', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component);

      component.continueToBooking(fakeDetail());

      expect(routerNavigate).not.toHaveBeenCalled();
    });

    it('the selection overlay tracks the narrowed Start/End, not the full segment (owner\'s correction)', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadWithOneSegment(component); // 08:00-17:00, axis rounds to the same 08:00-17:00 (9-hour span)

      component.selectSegment(component.dayRows()[0].segments[0]);
      // Default selection (Start + the 240-min max) starts at the axis's own
      // left edge but is narrower than the full 9-hour segment/axis.
      expect(component.selectionOverlayLeftPercent()).toBeCloseTo(0);
      expect(component.selectionOverlayWidthPercent()).toBeCloseTo((240 / 540) * 100, 1);

      component.onSelectedStartChange(inputChangeEvent(String(9 * 60))); // 09:00, 1h into the 9h axis
      component.onSelectedEndChange(inputChangeEvent(String(11 * 60))); // 11:00

      // (9:00-8:00)/9h = 11.11%, (11:00-9:00)/9h = 22.22% wide — narrower
      // than the full bar, proving the overlay shrinks with the selection
      // instead of staying burgundy across the whole original segment.
      expect(component.selectionOverlayLeftPercent()).toBeCloseTo((60 / 540) * 100, 1);
      expect(component.selectionOverlayWidthPercent()).toBeCloseTo((120 / 540) * 100, 1);
    });

    describe('dragging the selection handles', () => {
      it('dragging the start handle moves selectedStartMinutes, snapped to 15 minutes', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        // Unbounded duration limits so the drag math below isn't also
        // constrained by them — that's covered in its own test.
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]); // axis: 08:00-17:00 (540 min)

        const handle = fakeHandleInTrack(600); // 600px == 540 minutes
        component.onHandlePointerDown('start', fakePointerDownEvent(handle));
        // 50% across -> 270 min from 08:00 -> 12:30, already on a 15-min step.
        component.onHandlePointerMove('start', fakePointerMoveEvent(300));

        expect(component.selectedStartMinutes()).toBe(12 * 60 + 30);
      });

      it('dragging is clamped to the clicked segment\'s own bounds', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);

        const handle = fakeHandleInTrack(600);
        component.onHandlePointerDown('start', fakePointerDownEvent(handle));
        // Dragging past the left edge (negative clientX) can't push Start
        // before the segment's own 08:00 start.
        component.onHandlePointerMove('start', fakePointerMoveEvent(-500));

        expect(component.selectedStartMinutes()).toBe(8 * 60);
      });

      it('dragging respects the resource\'s own minDurationMinutes relative to the other edge', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: 60, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);
        component.onSelectedEndChange(inputChangeEvent(String(9 * 60))); // End = 09:00

        const handle = fakeHandleInTrack(600);
        component.onHandlePointerDown('start', fakePointerDownEvent(handle));
        // Try to drag Start to 08:45 — only 15 minutes before the 09:00 End,
        // short of the resource's own 60-minute minimum.
        const fraction = (8 * 60 + 45 - 8 * 60) / 540;
        component.onHandlePointerMove('start', fakePointerMoveEvent(fraction * 600));

        // Clamped to the latest Start that still leaves a full hour: 08:00.
        expect(component.selectedStartMinutes()).toBe(8 * 60);
      });

      it('pointerup stops the drag — a further pointermove has no effect', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);

        const handle = fakeHandleInTrack(600);
        component.onHandlePointerDown('start', fakePointerDownEvent(handle));
        component.onHandlePointerUp();
        component.onHandlePointerMove('start', fakePointerMoveEvent(300));

        expect(component.selectedStartMinutes()).toBe(8 * 60); // unchanged
      });

      it('a pointermove for the other edge is ignored while dragging one handle', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);

        const handle = fakeHandleInTrack(600);
        component.onHandlePointerDown('start', fakePointerDownEvent(handle));
        component.onHandlePointerMove('end', fakePointerMoveEvent(300)); // wrong edge

        expect(component.selectedEndMinutes()).toBe(17 * 60); // untouched
      });
    });

    describe('dragging the whole selection window (owner\'s follow-up request)', () => {
      it('moves Start and End together by the same snapped amount, preserving the duration', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]); // defaults to the whole segment (no max)
        component.onSelectedStartChange(inputChangeEvent(String(9 * 60))); // narrow to 09:00-...
        component.onSelectedEndChange(inputChangeEvent(String(11 * 60))); // ...-11:00 (2h window)

        const overlay = fakeHandleInTrack(600); // 600px == the 540-min axis
        component.onOverlayPointerDown(fakePointerDownEvent(overlay));
        // Drag 100px right -> (100/600)*540 = 90 minutes, already a 15-min multiple.
        component.onOverlayPointerMove(fakePointerMoveEvent(100));

        expect(component.selectedStartMinutes()).toBe(9 * 60 + 90);
        expect(component.selectedEndMinutes()).toBe(11 * 60 + 90);
      });

      it('snaps the drag delta to 15-minute steps', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);
        component.onSelectedStartChange(inputChangeEvent(String(9 * 60)));
        component.onSelectedEndChange(inputChangeEvent(String(11 * 60)));

        const overlay = fakeHandleInTrack(600);
        component.onOverlayPointerDown(fakePointerDownEvent(overlay));
        // 10px -> (10/600)*540 = 9 minutes -> rounds to 15.
        component.onOverlayPointerMove(fakePointerMoveEvent(10));

        expect(component.selectedStartMinutes()).toBe(9 * 60 + 15);
      });

      it('clamps the window so it can\'t be dragged past either edge of the segment', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]); // 08:00-17:00, no max -> whole segment
        component.onSelectedEndChange(inputChangeEvent(String(9 * 60))); // narrow to 08:00-09:00 (1h)

        const overlay = fakeHandleInTrack(600);
        component.onOverlayPointerDown(fakePointerDownEvent(overlay));
        // A huge drag right, far past the segment's own 17:00 end.
        component.onOverlayPointerMove(fakePointerMoveEvent(5000));

        // Window (1h wide) pinned against the segment's own end: 16:00-17:00.
        expect(component.selectedStartMinutes()).toBe(16 * 60);
        expect(component.selectedEndMinutes()).toBe(17 * 60);
      });

      it('pointerup stops a window drag the same as an edge drag', () => {
        const fixture = createFixture();
        const component = fixture.componentInstance as TestableAvailabilityComponent;
        httpMock.expectOne(`${API}/resources/r1`).flush(
          fakeDetail({ timeZoneId: 'UTC', minDurationMinutes: null, maxDurationMinutes: null }),
        );
        httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
          fakeAvailability({
            intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
          }),
        );
        component.selectSegment(component.dayRows()[0].segments[0]);
        component.onSelectedEndChange(inputChangeEvent(String(9 * 60)));

        const overlay = fakeHandleInTrack(600);
        component.onOverlayPointerDown(fakePointerDownEvent(overlay));
        component.onHandlePointerUp();
        component.onOverlayPointerMove(fakePointerMoveEvent(300));

        expect(component.selectedStartMinutes()).toBe(8 * 60); // unchanged
      });
    });
  });

  describe('clicking elsewhere clears the selection (owner\'s follow-up request)', () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-09-21T10:00:00Z'));
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    function loadAndSelect(component: TestableAvailabilityComponent): DaySegment {
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(
        fakeAvailability({
          intervals: [{ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T17:00:00Z', remainingCapacity: 1 }],
        }),
      );
      const segment = component.dayRows()[0].segments[0];
      component.selectSegment(segment);
      return segment;
    }

    it('clears the selection on a click outside the grid entirely', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadAndSelect(component);

      component.onDocumentClick(clickEventWithTarget(document.createElement('button')));

      expect(component.selectedSegment()).toBeNull();
    });

    it('does not clear the selection when the click lands on a bookable bar (switching segments has its own handling)', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadAndSelect(component);

      const bar = document.createElement('button');
      bar.className = 'segment';
      component.onDocumentClick(clickEventWithTarget(bar));

      expect(component.selectedSegment()).not.toBeNull();
    });

    it('does not clear the selection when the click lands on the selection overlay/handles', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadAndSelect(component);

      const overlayChild = document.createElement('span');
      overlayChild.className = 'selection-handle';
      const overlay = document.createElement('div');
      overlay.className = 'selection-overlay';
      overlay.appendChild(overlayChild);

      component.onDocumentClick(clickEventWithTarget(overlayChild));

      expect(component.selectedSegment()).not.toBeNull();
    });

    it('does not clear the selection when the click lands inside the selection panel (e.g. the Start/End dropdowns)', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      loadAndSelect(component);

      const panel = document.createElement('div');
      panel.className = 'selection-panel';
      const select = document.createElement('select');
      panel.appendChild(select);

      component.onDocumentClick(clickEventWithTarget(select));

      expect(component.selectedSegment()).not.toBeNull();
    });

    it('does nothing when there is no selection to clear', () => {
      const fixture = createFixture();
      const component = fixture.componentInstance as TestableAvailabilityComponent;
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeDetail({ timeZoneId: 'UTC' }));
      httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`).flush(fakeAvailability());

      // Should not throw, and should not interfere with the (unrelated)
      // range-picker click-outside handling in the same listener.
      expect(() => component.onDocumentClick(clickEventWithTarget(document.createElement('button')))).not.toThrow();
      expect(component.selectedSegment()).toBeNull();
    });
  });
});
