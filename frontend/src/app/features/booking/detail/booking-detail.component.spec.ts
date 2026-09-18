import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { BookingDetailComponent } from './booking-detail.component';
import { BookingDetail } from '../booking.models';
import { ResourceDetail } from '../../resources/resources.models';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';

const API = 'http://localhost:5270';

// The viewer's own zone, whatever the suite happens to run in — the screen
// reads instants against it, so the tests have to name it rather than assume
// UTC (CI) or CET (local).
const VIEWER_ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;

function localInstant(year: number, monthIndex: number, day: number, hours = 0, minutes = 0): string {
  return new Date(year, monthIndex, day, hours, minutes).toISOString();
}

function fakeBooking(overrides: Partial<BookingDetail> = {}): BookingDetail {
  return {
    id: 'b1',
    resourceId: 'r1',
    resourceName: 'Conference Room A',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: localInstant(2026, 8, 24, 9, 0),
    endsAtUtc: localInstant(2026, 8, 24, 10, 30),
    quantity: 1,
    title: null,
    status: 'Confirmed',
    checkedInAtUtc: null,
    cancelledByUserId: null,
    cancelledAtUtc: null,
    cancellationReason: null,
    createdAtUtc: localInstant(2026, 8, 17, 9, 0),
    updatedAtUtc: localInstant(2026, 8, 17, 9, 0),
    approval: null,
    ...overrides,
  };
}

function fakeResource(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: null,
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: VIEWER_ZONE,
    requiresApproval: false,
    minDurationMinutes: null,
    maxDurationMinutes: null,
    isArchived: false,
    createdAtUtc: localInstant(2026, 1, 1),
    updatedAtUtc: localInstant(2026, 1, 1),
    availabilityWindows: [],
    approvers: [],
    ...overrides,
  };
}

describe('BookingDetailComponent', () => {
  let httpMock: HttpTestingController;
  let paramMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let breadcrumbService: BreadcrumbService;
  let fixture: ComponentFixture<BookingDetailComponent>;

  function createFixture(id = 'b1') {
    paramMap$ = new BehaviorSubject(convertToParamMap({ id }));

    TestBed.configureTestingModule({
      imports: [BookingDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id }) }, paramMap: paramMap$ },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    breadcrumbService = TestBed.inject(BreadcrumbService);
    fixture = TestBed.createComponent(BookingDetailComponent);
    fixture.detectChanges();
    return fixture;
  }

  function root(): HTMLElement {
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return (root().textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  // Loads the booking, then the resource behind it. The second is best-effort:
  // it exists only to name the resource's own timezone, which the booking
  // response does not carry.
  function load(booking: Partial<BookingDetail> = {}, resource: Partial<ResourceDetail> | null = {}) {
    const body = fakeBooking(booking);
    httpMock.expectOne(`${API}/bookings/${body.id}`).flush(body);

    if (resource === null) {
      httpMock
        .expectOne(`${API}/resources/${body.resourceId}`)
        .flush(null, { status: 500, statusText: 'Server Error' });
    } else {
      httpMock
        .expectOne(`${API}/resources/${body.resourceId}`)
        .flush(fakeResource({ id: body.resourceId, ...resource }));
    }
    return body;
  }

  afterEach(() => {
    httpMock.verify();
    breadcrumbService.setOverride(null);
  });

  describe('loading', () => {
    it('fetches the booking named by the route id', () => {
      createFixture('b7');

      const req = httpMock.expectOne(`${API}/bookings/b7`);
      expect(req.request.method).toBe('GET');
      req.flush(fakeBooking({ id: 'b7' }));
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeResource());
    });

    it('shows a loading state before anything arrives', () => {
      createFixture();
      expect(text()).toContain('Loading this booking');
      load();
    });

    // AC-4 inside one tenant: another member's booking, another tenant's, and a
    // nonexistent id are byte-identical 404s, so the wording distinguishes none
    // of them — and offers a way back rather than a retry that cannot help.
    it('shows the not-found state for a 404, distinct from a generic failure', () => {
      createFixture('missing');

      httpMock
        .expectOne(`${API}/bookings/missing`)
        .flush({ reasonCode: 'BookingNotFound' }, { status: 404, statusText: 'Not Found' });

      expect(text()).toContain("doesn't exist, or you don't have access");
      expect(root().querySelector('.retry-button')).toBeNull();
      expect(root().querySelector('.state-message--empty a')?.getAttribute('href')).toBe('/calendar');
    });

    it('offers a retry for any other failure, and re-fetches on click', () => {
      createFixture();

      httpMock.expectOne(`${API}/bookings/b1`).error(new ProgressEvent('error'));
      expect(text()).toContain("couldn't load this booking");

      (root().querySelector('.retry-button') as HTMLButtonElement).click();
      load({ title: 'Recovered' });

      expect(text()).toContain('Recovered');
    });

    it('re-fetches when the route id changes without recreating the component', () => {
      createFixture('b1');
      load({ id: 'b1', title: 'First' });

      paramMap$.next(convertToParamMap({ id: 'b2' }));
      load({ id: 'b2', title: 'Second' });

      expect(text()).toContain('Second');
      expect(text()).not.toContain('First');
    });

    it('puts the booking in the breadcrumb, and clears it on destroy', () => {
      createFixture();
      load({ title: 'Sprint review' });

      expect(breadcrumbService.override()).toBe('Sprint review');

      fixture.destroy();
      expect(breadcrumbService.override()).toBeNull();
    });

    it('falls back to the resource name in the breadcrumb for an untitled booking', () => {
      createFixture();
      load({ title: null });

      expect(breadcrumbService.override()).toBe('Conference Room A');
    });
  });

  describe('what it renders', () => {
    it('states the span, the duration and the resource', () => {
      createFixture();
      load();

      expect(text()).toContain('09:00 – 10:30');
      expect(text()).toContain('1 hour 30 minutes');
      expect(root().querySelector('.resource-link')?.getAttribute('href')).toBe('/resources/r1');
    });

    // The overnight case the shared span formatter exists to get right: the end
    // carries its own date rather than being read against the start's.
    it('carries the end date when a booking runs overnight', () => {
      createFixture();
      load({
        startsAtUtc: localInstant(2026, 8, 24, 22, 0),
        endsAtUtc: localInstant(2026, 8, 25, 2, 0),
      });

      expect(text()).toContain('22:00 – 02:00');
      expect(text()).toContain('Sep 25');
    });

    it('marks a series occurrence', () => {
      createFixture();
      load({ recurrenceRuleId: 'rr1' });

      expect(text()).toContain('Part of a repeating series');
    });

    // FR-7.1: the member has to be told the slot is not theirs yet, and in the
    // lead rather than as a footnote.
    it('says a pending booking is not held yet', () => {
      createFixture();
      load({ status: 'Pending' });

      expect(root().querySelector('.lead-note')?.textContent).toContain('not held for you yet');
    });

    it('says nothing of the sort for a confirmed booking', () => {
      createFixture();
      load({ status: 'Confirmed' });

      expect(root().querySelector('.lead-note')).toBeNull();
    });

    // The resource's zone is a second reading, and only worth showing when it
    // differs — otherwise it is the same fact stated twice.
    it('says nothing about timezones when the resource shares the viewer’s', () => {
      createFixture();
      load({}, { timeZoneId: VIEWER_ZONE });

      expect(root().querySelector('.zone-note')).toBeNull();
    });

    it('adds the resource’s own reading when the two zones differ', () => {
      createFixture();
      load({}, { timeZoneId: VIEWER_ZONE === 'UTC' ? 'Asia/Tokyo' : 'UTC' });

      expect(root().querySelector('.zone-note')?.textContent).toContain("resource's timezone");
    });

    // The resource fetch is best-effort: it only names a timezone, so losing it
    // must cost one line rather than the screen.
    it('still renders everything else when the resource cannot be loaded', () => {
      createFixture();
      load({ title: 'Sprint review' }, null);

      expect(text()).toContain('Sprint review');
      expect(text()).toContain('09:00 – 10:30');
      expect(root().querySelector('.zone-note')).toBeNull();
      expect(root().querySelector('.state-message--error')).toBeNull();
    });
  });

  // Decision 0002 records the actor separately from the owner precisely so
  // these read differently. All three are reachable only by direct link now —
  // the calendar does not draw a cancelled booking — which is exactly why the
  // screen still has to get them right.
  describe('the three cancellation readings', () => {
    it('reads a self-cancellation as the member’s own', () => {
      createFixture();
      load({
        status: 'Cancelled',
        userId: 'u1',
        cancelledByUserId: 'u1',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
      });

      expect(root().querySelector('.cancel-lead')?.textContent).toContain('You cancelled this booking');
    });

    it('reads a different actor as an administrator', () => {
      createFixture();
      load({
        status: 'Cancelled',
        userId: 'u1',
        cancelledByUserId: 'admin-9',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
      });

      expect(root().querySelector('.cancel-lead')?.textContent).toContain(
        'An administrator cancelled this booking',
      );
    });

    // Booking.CancelForBlackout leaves the actor null on purpose — there is no
    // person behind it — so a null here is the blackout case, not missing data.
    it('reads a null actor as the resource being made unavailable, and shows the reason', () => {
      createFixture();
      load({
        status: 'Cancelled',
        cancelledByUserId: null,
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
        cancellationReason: 'Blackout: annual maintenance',
      });

      expect(root().querySelector('.cancel-lead')?.textContent).toContain(
        'the resource was made unavailable',
      );
      expect(root().querySelector('.cancel-reason')?.textContent).toContain(
        'Blackout: annual maintenance',
      );
    });

    it('shows no cancellation section for a live booking', () => {
      createFixture();
      load({ status: 'Confirmed' });

      expect(root().querySelector('.card--cancelled')).toBeNull();
    });
  });

  describe('the approval section', () => {
    it('is absent when the resource never required approval', () => {
      createFixture();
      load({ approval: null });

      expect(text()).not.toContain('Approval');
    });

    it('shows a pending request with its expiry', () => {
      createFixture();
      load({
        status: 'Pending',
        approval: {
          approvalRequestId: 'a1',
          requestedAtUtc: localInstant(2026, 8, 17, 9, 0),
          expiresAtUtc: localInstant(2026, 8, 18, 9, 0),
          decision: 'Pending',
          decidedByUserId: null,
          decidedAtUtc: null,
          note: null,
        },
      });

      expect(text()).toContain('Requested');
      expect(text()).toContain('Expires');
    });

    // FR-7.4: a tenant with no configured expiry leaves requests open
    // indefinitely — a real configuration, not a missing value, so it gets a
    // sentence rather than a blank.
    it('says so when a request never expires', () => {
      createFixture();
      load({
        status: 'Pending',
        approval: {
          approvalRequestId: 'a1',
          requestedAtUtc: localInstant(2026, 8, 17, 9, 0),
          expiresAtUtc: null,
          decision: 'Pending',
          decidedByUserId: null,
          decidedAtUtc: null,
          note: null,
        },
      });

      expect(text()).toContain('No expiry');
    });

    // Withdrawn is not a person's judgment — the booking stopped existing to
    // decide on. Seen whenever a Pending booking is cancelled, which is this
    // phase's own step 5.
    it('explains a withdrawn decision rather than leaving it bare', () => {
      createFixture();
      load({
        status: 'Cancelled',
        cancelledByUserId: 'u1',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
        approval: {
          approvalRequestId: 'a1',
          requestedAtUtc: localInstant(2026, 8, 17, 9, 0),
          expiresAtUtc: null,
          decision: 'Withdrawn',
          decidedByUserId: null,
          decidedAtUtc: localInstant(2026, 8, 20, 11, 0),
          note: null,
        },
      });

      expect(text()).toContain('cancelled before it was decided');
    });

    it('shows an approver’s note when there is one', () => {
      createFixture();
      load({
        approval: {
          approvalRequestId: 'a1',
          requestedAtUtc: localInstant(2026, 8, 17, 9, 0),
          expiresAtUtc: null,
          decision: 'Approved',
          decidedByUserId: 'approver-1',
          decidedAtUtc: localInstant(2026, 8, 18, 9, 0),
          note: 'Fine by me',
        },
      });

      expect(text()).toContain('Fine by me');
    });
  });
});
