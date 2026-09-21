import { ComponentFixture, TestBed } from '@angular/core/testing';
import { computed, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { BookingDetailComponent } from '../components/booking-detail/booking-detail.component';
import { BookingDetail, CancelBookingResponse } from '../models/booking.models';
import { ResourceDetail } from '../../resources/models/resources.models';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { AuthService } from '../../../core/auth/auth.service';

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

function cancelResponse(overrides: Partial<CancelBookingResponse> = {}): CancelBookingResponse {
  return {
    id: 'b1',
    resourceId: 'r1',
    userId: 'u1',
    startsAtUtc: localInstant(2026, 8, 24, 9, 0),
    endsAtUtc: localInstant(2026, 8, 24, 10, 30),
    quantity: 1,
    title: null,
    status: 'Cancelled',
    cancelledByUserId: 'u1',
    cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
    cancellationReason: null,
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

// The screen asks who is reading it (decision `0027` gave it two audiences), so
// every fixture has to say. A fake rather than the real AuthService, which
// reads its claims from localStorage — empty in jsdom, which would silently
// make every viewer "not the owner" and hide the cancel actions these tests are
// about.
//
// Defaults to the booking's own owner, because that is who this screen was
// built for and what every test written before 2026-09-21 assumed.
class FakeAuthService {
  readonly claims = signal<{ sub: string; roles: string[] } | null>({
    sub: 'u1',
    roles: ['Member'],
  });

  readonly canApproveBookings = computed(() => {
    const roles = this.claims()?.roles ?? [];
    return roles.includes('Approver') || roles.includes('TenantAdmin');
  });
}

describe('BookingDetailComponent', () => {
  let httpMock: HttpTestingController;
  let paramMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let breadcrumbService: BreadcrumbService;
  let fixture: ComponentFixture<BookingDetailComponent>;
  let auth: FakeAuthService;

  // `viewer` names who is signed in: the owner by default, or an approver
  // looking at someone else's request.
  function createFixture(id = 'b1', viewer: 'owner' | 'approver' = 'owner') {
    paramMap$ = new BehaviorSubject(convertToParamMap({ id }));
    auth = new FakeAuthService();
    if (viewer === 'approver') {
      auth.claims.set({ sub: 'approver-1', roles: ['Approver'] });
    }

    TestBed.configureTestingModule({
      imports: [BookingDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: auth as unknown as AuthService },
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

  // Step 5. Every assertion is against the rendered DOM.
  describe('cancelling', () => {
    // The action is offered from `Booking.CanBeCancelled` mirrored: not
    // terminal, and not already ended.
    function cancelButton(): HTMLButtonElement | null {
      return root().querySelector('.cancel-box .danger-button');
    }

    function future() {
      const start = new Date(Date.now() + 86_400_000);
      return {
        startsAtUtc: start.toISOString(),
        endsAtUtc: new Date(start.getTime() + 3_600_000).toISOString(),
      };
    }

    function past() {
      const start = new Date(Date.now() - 86_400_000);
      return {
        startsAtUtc: start.toISOString(),
        endsAtUtc: new Date(start.getTime() + 3_600_000).toISOString(),
      };
    }

    function openConfirm() {
      cancelButton()!.click();
      fixture.detectChanges();
    }

    function confirm() {
      const buttons = Array.from(root().querySelectorAll('.confirm-actions button')) as HTMLButtonElement[];
      buttons.find((b) => b.textContent?.includes('Yes, cancel it'))!.click();
      fixture.detectChanges();
    }

    it.each(['Confirmed', 'Pending'] as const)('offers the action for a live %s booking', (status) => {
      createFixture();
      load({ status, ...future() });

      expect(cancelButton()?.textContent?.trim()).toBe('Cancel booking');
    });

    // Both halves of the rule, each on its own.
    it.each(['Cancelled', 'Rejected', 'Completed', 'NoShow'] as const)(
      'offers nothing for a terminal %s booking',
      (status) => {
        createFixture();
        load({ status, ...future() });

        expect(cancelButton()).toBeNull();
      },
    );

    // The test is on EndsAtUtc, not StartsAtUtc — a meeting under way can still
    // be called off, because the room is free from then on.
    it('offers nothing once the booking has ended', () => {
      createFixture();
      load({ status: 'Confirmed', ...past() });

      expect(cancelButton()).toBeNull();
    });

    it('still offers it for a booking already under way', () => {
      createFixture();
      load({
        status: 'Confirmed',
        startsAtUtc: new Date(Date.now() - 600_000).toISOString(),
        endsAtUtc: new Date(Date.now() + 600_000).toISOString(),
      });

      expect(cancelButton()).not.toBeNull();
    });

    // A single "Cancel booking" on a booking that belongs to eleven others
    // would be exactly the ambiguous button the plan rules out. Step 6 adds the
    // series option beside it; this wording is unambiguous on its own already.
    it('says which one it cancels for a series occurrence', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      expect(cancelButton()?.textContent?.trim()).toBe('Cancel this occurrence');
    });

    it('confirms before acting, and sends nothing until confirmed', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      expect(text()).toContain('released immediately');
      httpMock.expectNone(`${API}/bookings/b1/cancel`);
    });

    it('backs out without sending anything', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      (Array.from(root().querySelectorAll('.confirm-actions button')) as HTMLButtonElement[])
        .find((b) => b.textContent?.includes('Keep it'))!
        .click();
      fixture.detectChanges();

      expect(root().querySelector('.confirm-actions')).toBeNull();
      expect(cancelButton()).not.toBeNull();
    });

    it('posts an optional reason, omitted as null when blank', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      confirm();

      const req = httpMock.expectOne(`${API}/bookings/b1/cancel`);
      expect(req.request.body).toEqual({ reason: null });
      req.flush(cancelResponse());
    });

    it('sends the reason the member typed', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      const box = root().querySelector('.reason-input') as HTMLTextAreaElement;
      box.value = '  Meeting moved  ';
      box.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      confirm();

      const req = httpMock.expectOne(`${API}/bookings/b1/cancel`);
      expect(req.request.body).toEqual({ reason: 'Meeting moved' });
      req.flush(cancelResponse({ cancellationReason: 'Meeting moved' }));
    });

    // Not idempotent: a second call rewrites who cancelled it. So the control
    // is disabled in flight rather than merely ignored.
    it('blocks a second submit while the first is in flight', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      confirm();
      httpMock.expectOne(`${API}/bookings/b1/cancel`);

      const button = (Array.from(root().querySelectorAll('.confirm-actions button')) as HTMLButtonElement[])
        .find((b) => b.textContent?.includes('Cancelling'));
      expect(button?.disabled).toBe(true);

      button!.click();
      httpMock.verify(); // no second request
    });

    // Updated from the response, not by re-fetching — it carries the
    // cancellation trio precisely so a client need not ask again.
    it('updates the screen from the response without a second request', () => {
      createFixture();
      load({ status: 'Confirmed', userId: 'u1', ...future() });

      openConfirm();
      confirm();
      httpMock
        .expectOne(`${API}/bookings/b1/cancel`)
        .flush(cancelResponse({ cancelledByUserId: 'u1', cancellationReason: 'Meeting moved' }));
      fixture.detectChanges();

      expect(text()).toContain('the time is free again');
      expect(root().querySelector('.cancel-lead')?.textContent).toContain('You cancelled this booking');
      expect(root().querySelector('.cancel-reason')?.textContent).toContain('Meeting moved');
      // The action is gone — it is no longer cancellable.
      expect(cancelButton()).toBeNull();
    });

    // The one thing the cancel response does not carry. Leaving the approval
    // showing "Pending" on a cancelled booking would be a visible lie;
    // ApprovalRequest.Withdraw is what the server actually does, verified live.
    it('marks a pending approval withdrawn once the booking is cancelled', () => {
      createFixture();
      load({
        status: 'Pending',
        userId: 'u1',
        ...future(),
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

      openConfirm();
      confirm();
      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(cancelResponse({ cancelledByUserId: 'u1' }));
      fixture.detectChanges();

      expect(text()).toContain('cancelled before it was decided');
      expect(text()).not.toContain('Expires');
    });

    it('renders a refusal in the cancel vocabulary, with no retry', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      confirm();
      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(
        { status: 422, title: 'Refused.', reasonCode: 'BookingNotCancellable' },
        { status: 422, statusText: 'Unprocessable Content' },
      );
      fixture.detectChanges();

      const error = root().querySelector('.cancel-error');
      expect(error?.textContent).toContain('already ended or already been cancelled');
      // The only way out is to look again — never "try again", which would
      // rewrite the actor if it landed.
      expect(error?.querySelector('button')?.textContent).toContain('Reload');
      expect(error?.textContent).not.toContain('Try again');
    });

    it('says an unknown outcome is unknown, and sends the member to reload', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      confirm();
      httpMock.expectOne(`${API}/bookings/b1/cancel`).error(new ProgressEvent('error'));
      fixture.detectChanges();

      expect(root().querySelector('.cancel-error')?.textContent).toContain(
        'may or may not have gone through',
      );
    });

    it('refuses an over-long reason before any request goes out', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      const box = root().querySelector('.reason-input') as HTMLTextAreaElement;
      box.value = 'x'.repeat(301);
      box.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(root().querySelector('.field-error')?.textContent).toContain('under 300 characters');
      confirm();
      httpMock.expectNone(`${API}/bookings/b1/cancel`);
    });

    // A server message about a control is cleared when that control is edited —
    // otherwise only the submit it is blocking could clear it.
    it('clears a server field message when the reason is edited', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      openConfirm();
      confirm();
      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(
        { status: 400, title: 'Invalid.', reasonCode: 'ValidationFailed', errors: { Reason: ['Too long.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();
      expect(text()).toContain('Too long.');

      const box = root().querySelector('.reason-input') as HTMLTextAreaElement;
      box.value = 'Shorter';
      box.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(text()).not.toContain('Too long.');
    });
  });

  // Step 6. FR-5.3: both cancellations reachable, neither implied by the other.
  describe('cancelling a whole series', () => {
    function future() {
      const start = new Date(Date.now() + 86_400_000);
      return {
        startsAtUtc: start.toISOString(),
        endsAtUtc: new Date(start.getTime() + 3_600_000).toISOString(),
      };
    }

    function past() {
      const start = new Date(Date.now() - 86_400_000);
      return {
        startsAtUtc: start.toISOString(),
        endsAtUtc: new Date(start.getTime() + 3_600_000).toISOString(),
      };
    }

    function buttons(): HTMLButtonElement[] {
      return Array.from(root().querySelectorAll('.cancel-box button'));
    }

    function clickByText(fragment: string) {
      buttons().find((b) => b.textContent?.includes(fragment))!.click();
      fixture.detectChanges();
    }

    function seriesResponse(cancelledBookingIds: string[]) {
      return {
        recurrenceRuleId: 'rr1',
        cancelledByUserId: 'u1',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
        cancelledBookingIds,
      };
    }

    // The ambiguous single button FR-5.3 rules out — never present, in either
    // direction.
    it('offers no series option on a one-off booking', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: null, ...future() });

      expect(buttons().map((b) => b.textContent?.trim())).toEqual(['Cancel booking']);
    });

    it('offers both, named unambiguously, on a series occurrence', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      expect(buttons().map((b) => b.textContent?.trim())).toEqual([
        'Cancel this occurrence',
        'Cancel the whole remaining series',
      ]);
    });

    // **The two rules are different and the client can only check one.**
    // `RecurrenceRule.CanBeCancelled()` is `Status == Active` with no time
    // component, so a live series stays cancellable from an occurrence that is
    // itself past or already cancelled.
    it('still offers the series option from a past occurrence', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...past() });

      expect(buttons().map((b) => b.textContent?.trim())).toEqual([
        'Cancel the whole remaining series',
      ]);
    });

    it('still offers the series option from an already-cancelled occurrence', () => {
      createFixture();
      load({
        status: 'Cancelled',
        recurrenceRuleId: 'rr1',
        cancelledByUserId: 'u1',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
        ...future(),
      });

      expect(buttons().map((b) => b.textContent?.trim())).toEqual([
        'Cancel the whole remaining series',
      ]);
    });

    // Stated *before* confirming, not explained afterwards — a member who
    // learns the reach from the result has already committed.
    it('says what "remaining" means before the member confirms', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');

      const lead = root().querySelector('.confirm-lead')?.textContent ?? '';
      expect(lead).toContain('Every occurrence still to come is cancelled');
      expect(lead).toContain('already finished are left as they are');
      httpMock.expectNone(`${API}/recurrence-rules/rr1/cancel`);
    });

    it('says the opposite for the single-occurrence confirmation', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel this occurrence');

      expect(root().querySelector('.confirm-lead')?.textContent).toContain(
        'The rest of the series is unaffected',
      );
    });

    it('posts to the series endpoint with the reason', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      const box = root().querySelector('.reason-input') as HTMLTextAreaElement;
      box.value = 'Project finished';
      box.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      clickByText('Yes, cancel the series');

      const req = httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ reason: 'Project finished' });
      req.flush(seriesResponse(['b1', 'b2', 'b3']));
    });

    // Reported from the ids, not as a bare success — which is why the endpoint
    // returns ids rather than a count.
    it('reports how many occurrences were actually freed', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock
        .expectOne(`${API}/recurrence-rules/rr1/cancel`)
        .flush(seriesResponse(['b1', 'b2', 'b3']));
      fixture.detectChanges();

      expect(text()).toContain('3 upcoming occurrences were freed');
    });

    it('uses the singular for a series with one occurrence left', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).flush(seriesResponse(['b1']));
      fixture.detectChanges();

      expect(text()).toContain('1 upcoming occurrence was freed');
    });

    // A legitimate outcome, not a failure: the rule was still Active but every
    // occurrence had already finished.
    it('says plainly when the series had nothing left to free', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...past() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).flush(seriesResponse([]));
      fixture.detectChanges();

      expect(text()).toContain('no upcoming occurrences left, so no time was freed');
    });

    it('marks this booking cancelled when the response says it was one of them', () => {
      createFixture();
      load({ status: 'Confirmed', userId: 'u1', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).flush(seriesResponse(['b1', 'b2']));
      fixture.detectChanges();

      expect(root().querySelector('.cancel-lead')?.textContent).toContain('You cancelled this booking');
    });

    // **The case that would read as a bug if it were got wrong.** A finished
    // occurrence survives a series cancel, so crossing it out anyway would be
    // the screen contradicting the server.
    it('leaves this booking alone when it was not among the cancelled', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...past() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock
        .expectOne(`${API}/recurrence-rules/rr1/cancel`)
        .flush(seriesResponse(['other-1', 'other-2']));
      fixture.detectChanges();

      expect(root().querySelector('.card--cancelled')).toBeNull();
      expect(text()).toContain('2 upcoming occurrences were freed');
    });

    // The series dialect, not the booking one: `RecurrenceRule.CanBeCancelled`
    // has no time component, so this means exactly one thing and says so.
    it('renders a series refusal in the series vocabulary', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).flush(
        { status: 422, title: 'Refused.', reasonCode: 'RecurrenceRuleNotCancellable' },
        { status: 422, statusText: 'Unprocessable Content' },
      );
      fixture.detectChanges();

      expect(root().querySelector('.cancel-error')?.textContent).toContain(
        'This series has already been cancelled',
      );
    });

    it('offers no retry on an unknown series outcome either', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      clickByText('Yes, cancel the series');
      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).error(new ProgressEvent('error'));
      fixture.detectChanges();

      const error = root().querySelector('.cancel-error');
      expect(error?.textContent).toContain('may or may not have gone through');
      expect(error?.querySelector('button')?.textContent).toContain('Reload');
    });

    it('backs out of the series confirmation without sending anything', () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      clickByText('Cancel the whole remaining series');
      clickByText('Keep it');

      expect(buttons().map((b) => b.textContent?.trim())).toEqual([
        'Cancel this occurrence',
        'Cancel the whole remaining series',
      ]);
      httpMock.expectNone(`${API}/recurrence-rules/rr1/cancel`);
    });
  });

  // Step 7's sweep. "Focus handled on the confirm affordance" is the item the
  // phase plan names, and it is the one thing on this screen a mouse user never
  // notices being wrong.
  describe('keyboard and screen-reader handling', () => {
    function future() {
      const start = new Date(Date.now() + 86_400_000);
      return {
        startsAtUtc: start.toISOString(),
        endsAtUtc: new Date(start.getTime() + 3_600_000).toISOString(),
      };
    }

    function click(fragment: string) {
      (Array.from(root().querySelectorAll('.cancel-box button')) as HTMLButtonElement[])
        .find((b) => b.textContent?.includes(fragment))!
        .click();
      fixture.detectChanges();
    }

    // afterNextRender runs as a microtask after the render, so these await it.
    async function settle() {
      await fixture.whenStable();
      fixture.detectChanges();
    }

    it('moves focus onto the heading that says which cancellation it is', async () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      click('Cancel the whole remaining series');
      await settle();

      const heading = root().querySelector('.confirm-heading');
      expect(document.activeElement).toBe(heading);
      expect(heading?.textContent).toContain('Cancel the whole remaining series?');
    });

    // Backing out must not drop focus on <body>, which sends a keyboard user
    // back to the top of the page.
    it('returns focus to the button it came from when backing out', async () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      click('Cancel the whole remaining series');
      await settle();
      click('Keep it');
      await settle();

      expect((document.activeElement as HTMLElement)?.textContent).toContain(
        'Cancel the whole remaining series',
      );
    });

    it('returns focus to the occurrence button for the occurrence confirmation', async () => {
      createFixture();
      load({ status: 'Confirmed', recurrenceRuleId: 'rr1', ...future() });

      click('Cancel this occurrence');
      await settle();
      click('Keep it');
      await settle();

      expect((document.activeElement as HTMLElement)?.textContent).toContain(
        'Cancel this occurrence',
      );
    });

    // The panel the member was standing in is replaced by the outcome, so focus
    // moves to what replaced it.
    it('moves focus to the outcome once the cancellation succeeds', async () => {
      createFixture();
      load({ status: 'Confirmed', userId: 'u1', ...future() });

      click('Cancel booking');
      await settle();
      click('Yes, cancel it');
      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(cancelResponse({ cancelledByUserId: 'u1' }));
      await settle();

      expect(document.activeElement).toBe(root().querySelector('.cancel-done'));
    });

    it('announces the outcome and the failure to assistive technology', () => {
      createFixture();
      load({ status: 'Confirmed', userId: 'u1', ...future() });

      click('Cancel booking');
      click('Yes, cancel it');
      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(cancelResponse({ cancelledByUserId: 'u1' }));
      fixture.detectChanges();

      expect(root().querySelector('.cancel-done')?.getAttribute('role')).toBe('status');
    });

    it('marks the reason box invalid and points at its message', () => {
      createFixture();
      load({ status: 'Confirmed', ...future() });

      click('Cancel booking');
      const box = root().querySelector('.reason-input') as HTMLTextAreaElement;
      box.value = 'x'.repeat(301);
      box.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(box.getAttribute('aria-invalid')).toBe('true');
      expect(box.getAttribute('aria-describedby')).toBe('cancel-reason-error');
      expect(root().querySelector('#cancel-reason-error')).not.toBeNull();
    });

    // Status is carried by the word, not the colour — the rule the whole phase
    // follows, checked here at the badge as well as at the chips.
    it('names the status in text rather than only colouring it', () => {
      createFixture();
      load({ status: 'Pending', ...future() });

      expect(root().querySelector('.badge')?.textContent?.trim()).toBe('Pending');
    });

    it('spells out NoShow rather than showing the enum name', () => {
      createFixture();
      load({ status: 'NoShow', ...future() });

      expect(root().querySelector('.badge')?.textContent?.trim()).toBe('No-show');
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

  // ---- Two audiences (decision 0027, 2026-09-21) -------------------------
  //
  // This screen was built for the booking's owner and every second-person
  // string on it assumed that. Decision `0027` made it reachable by an approver
  // reading a request on a resource they gate, and the screen had no way to
  // tell them apart — which the owner found by clicking, not the suite. These
  // tests assert the rendered words, because the words were the bug.
  describe('who is reading it', () => {
    it("tells the owner the time is not held for them", () => {
      createFixture('b1', 'owner');
      load({ status: 'Pending' });

      expect(text()).toContain('not held for you yet');
    });

    // The exact sentence an approver was being shown about someone else's
    // request. It has to be gone, not merely supplemented.
    it('never tells an approver the time is not held for *them*', () => {
      createFixture('b1', 'approver');
      load({ status: 'Pending' });

      expect(text()).not.toContain('not held for you yet');
      expect(text()).toContain('waiting for a decision');
      expect(text()).toContain('not held yet');
    });

    it('names the requester to an approver, and not to the owner', () => {
      createFixture('b1', 'approver');
      load({ userName: 'Member One' });

      expect(text()).toContain('Requested by');
      expect(text()).toContain('Member One');
    });

    it('does not print a member their own name on their own booking', () => {
      createFixture('b1', 'owner');
      load({ userName: 'Member One' });

      expect(text()).not.toContain('Requested by');
    });

    // "Back to your calendar" is wrong for an approver twice over: the booking
    // is not on their calendar, and the queue is where they came from.
    it('sends an approver back to the queue, and the owner to their calendar', () => {
      createFixture('b1', 'approver');
      load();

      expect(root().querySelector('.detail-actions a')?.getAttribute('href')).toBe('/approvals');
      expect(text()).toContain('Back to approvals');
    });

    it('sends the owner back to their calendar', () => {
      createFixture('b1', 'owner');
      load();

      expect(root().querySelector('.detail-actions a')?.getAttribute('href')).toBe('/calendar');
    });

    // `kind: 'self'` means *the owner cancelled it*, which is "you" only when
    // the owner is the one looking. The template rendered it unconditionally as
    // "You cancelled this booking" — flatly false read by anyone else.
    it('does not tell an approver that *they* cancelled a member\'s booking', () => {
      createFixture('b1', 'approver');
      load({
        status: 'Cancelled',
        userId: 'u1',
        userName: 'Member One',
        cancelledByUserId: 'u1',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
      });

      expect(text()).not.toContain('You cancelled this booking');
      expect(text()).toContain('Member One cancelled this booking');
    });

    it('still tells the owner that they cancelled it themselves', () => {
      createFixture('b1', 'owner');
      load({
        status: 'Cancelled',
        cancelledByUserId: 'u1',
        cancelledAtUtc: localInstant(2026, 8, 20, 11, 0),
      });

      expect(text()).toContain('You cancelled this booking');
    });

    // The more serious half of the same bug: an action offered that cannot
    // work. An approver's reach widens what they may read, never what they may
    // cancel (decision `0002`), so the server answers 404 — verified against
    // the running API when `0027` landed.
    it('offers no cancel action to an approver', () => {
      createFixture('b1', 'approver');
      load({ status: 'Confirmed', endsAtUtc: localInstant(2036, 0, 1, 10, 0) });

      expect(root().querySelector('.cancel-box')).toBeNull();
    });

    it('still offers the owner the cancel action', () => {
      createFixture('b1', 'owner');
      load({ status: 'Confirmed', endsAtUtc: localInstant(2036, 0, 1, 10, 0) });

      expect(root().querySelector('.cancel-box')).not.toBeNull();
    });

    it('offers no series cancel to an approver', () => {
      createFixture('b1', 'approver');
      load({ recurrenceRuleId: 'rule-1', endsAtUtc: localInstant(2036, 0, 1, 10, 0) });

      expect(root().querySelector('.cancel-box')).toBeNull();
    });
  });

  // ---- The decision panel on this screen (step 4) -------------------------
  describe('deciding from the booking', () => {
    function decisionPanel(): HTMLElement | null {
      return root().querySelector('app-decision-panel');
    }

    it('offers a decision to an approver on a pending request', () => {
      createFixture('b1', 'approver');
      load({ status: 'Pending' });

      expect(decisionPanel()).not.toBeNull();
      expect(text()).toContain('Your decision');
    });

    // Nobody decides on their own request. An approver booking a resource they
    // gate is ordinary and ApprovalReach does not exclude them, but this UI
    // does not invite it.
    it('offers no decision on the viewer\'s own booking', () => {
      createFixture('b1', 'owner');
      load({ status: 'Pending' });

      expect(decisionPanel()).toBeNull();
    });

    it('offers no decision once the request has been decided', () => {
      createFixture('b1', 'approver');
      load({ status: 'Confirmed' });

      expect(decisionPanel()).toBeNull();
    });

    // A decision re-reads the booking rather than patching it from the
    // response: `status` moves *and* the approval section gains its decision,
    // decider, timestamp and note, none of which the decision response carries.
    it('re-reads the booking after a decision', () => {
      createFixture('b1', 'approver');
      load({ status: 'Pending' });

      (
        Array.from(root().querySelectorAll('app-decision-panel button')) as HTMLButtonElement[]
      ).find((b) => b.textContent?.includes('Approve'))!.click();
      fixture.detectChanges();

      (
        Array.from(root().querySelectorAll('app-decision-panel button')) as HTMLButtonElement[]
      ).find((b) => b.textContent?.includes('Yes, approve it'))!.click();

      httpMock.expectOne(`${API}/bookings/b1/approve`).flush({
        id: 'b1',
        status: 'Confirmed',
        decidedByUserId: 'approver-1',
        decidedAtUtc: localInstant(2026, 8, 21, 11, 0),
      });

      // The re-read, which is the assertion — not the decision request itself.
      httpMock.expectOne(`${API}/bookings/b1`).flush(fakeBooking({ status: 'Confirmed' }));
      httpMock.expectOne(`${API}/resources/r1`).flush(fakeResource());

      expect(text()).toContain('Confirmed');
    });
  });
});
