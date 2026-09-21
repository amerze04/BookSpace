import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { BookingsService } from '../services/bookings.service';
import {
  ApproveBookingResponse,
  BookingDetail,
  BookingSummary,
  CancelBookingResponse,
  CreateBookingRequest,
  CreateBookingResponse,
} from '../models/booking.models';
import { PagedResult } from '../../../core/http/paged-result';
import { SKIP_ERROR_TOAST } from '../../../core/http/skip-error-toast';

const API = 'http://localhost:5270';

function request(overrides: Partial<CreateBookingRequest> = {}): CreateBookingRequest {
  return {
    resourceId: 'r1',
    startsAtUtc: '2026-09-21T13:00:00Z',
    endsAtUtc: '2026-09-21T14:00:00Z',
    quantity: 1,
    title: null,
    ...overrides,
  };
}

function confirmedResponse(overrides: Partial<CreateBookingResponse> = {}): CreateBookingResponse {
  return {
    id: 'b1',
    resourceId: 'r1',
    userId: 'u1',
    startsAtUtc: '2026-09-21T13:00:00Z',
    endsAtUtc: '2026-09-21T14:00:00Z',
    quantity: 1,
    title: null,
    status: 'Confirmed',
    createdAtUtc: '2026-09-17T09:00:00Z',
    approval: null,
    ...overrides,
  };
}

describe('BookingsService', () => {
  let service: BookingsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(BookingsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('posts the booking body to /bookings and opts out of the global error toast', async () => {
    const body = request({ title: 'Sprint review', quantity: 1 });
    const resultPromise = firstValueFrom(service.create(body));

    const req = httpMock.expectOne(`${API}/bookings`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    const response = confirmedResponse({ title: 'Sprint review' });
    req.flush(response, { status: 201, statusText: 'Created' });

    expect(await resultPromise).toEqual(response);
  });

  // The endpoint has no idempotency support at all (wp7-plan.md §7's flagged
  // gap). Asserted rather than assumed, because sending a key here would be
  // worse than not sending one: the backend ignores an unknown header, so a
  // client that believed it had one would then feel free to retry.
  it('sends no Idempotency-Key header — the one-off endpoint has none', () => {
    firstValueFrom(service.create(request()));

    const req = httpMock.expectOne(`${API}/bookings`);
    expect(req.request.headers.has('Idempotency-Key')).toBe(false);

    req.flush(confirmedResponse(), { status: 201, statusText: 'Created' });
  });

  // FR-7.1: an approval-gated resource answers Pending with the approval
  // detail attached, and the screen has to tell that apart from Confirmed.
  it('passes a Pending outcome through with its approval detail intact', async () => {
    const resultPromise = firstValueFrom(service.create(request({ resourceId: 'gated' })));

    const req = httpMock.expectOne(`${API}/bookings`);
    const response = confirmedResponse({
      resourceId: 'gated',
      status: 'Pending',
      approval: { approvalRequestId: 'a1', expiresAtUtc: '2026-09-19T09:00:00Z' },
    });
    req.flush(response, { status: 201, statusText: 'Created' });

    expect(await resultPromise).toEqual(response);
  });

  // FR-7.4: a tenant with no configured ApprovalExpiryHours leaves requests
  // pending indefinitely, so a null expiry beside a real approval id is a
  // legitimate state, not a missing value.
  it('accepts a Pending outcome whose approval never expires', async () => {
    const resultPromise = firstValueFrom(service.create(request()));

    const req = httpMock.expectOne(`${API}/bookings`);
    req.flush(
      confirmedResponse({
        status: 'Pending',
        approval: { approvalRequestId: 'a1', expiresAtUtc: null },
      }),
      { status: 201, statusText: 'Created' },
    );

    const result = await resultPromise;
    expect(result.approval?.expiresAtUtc).toBeNull();
  });

  it('surfaces a rejection as an error rather than swallowing it', async () => {
    const resultPromise = firstValueFrom(service.create(request()));

    const req = httpMock.expectOne(`${API}/bookings`);
    req.flush(
      {
        title: 'The request conflicts with existing state.',
        status: 409,
        reasonCode: 'SlotUnavailable',
        correlationId: 'c1',
      },
      { status: 409, statusText: 'Conflict' },
    );

    await expect(resultPromise).rejects.toMatchObject({
      status: 409,
      error: { reasonCode: 'SlotUnavailable' },
    });
  });

  describe('list', () => {
    // Decision 0015: an omitted filter is genuinely absent from the URL, so the
    // backend's own documented default applies rather than a second copy of it
    // invented here. Asserted as "no query string at all" rather than
    // field-by-field, because a default that leaked in would show up as one.
    it('sends no query string when no filter was set', () => {
      firstValueFrom(service.list());

      const req = httpMock.expectOne(`${API}/bookings`);
      expect(req.request.method).toBe('GET');
      expect(req.request.params.keys()).toEqual([]);
      expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

      req.flush(page());
    });

    it('sends every filter it was given, and nothing it was not', () => {
      firstValueFrom(
        service.list({
          from: '2026-09-18T00:00:00Z',
          status: 'Confirmed',
          page: 2,
          pageSize: 20,
          sort: '-startsAtUtc',
        }),
      );

      const req = httpMock.expectOne(
        (r) => r.url === `${API}/bookings` && r.params.get('status') === 'Confirmed',
      );
      expect(req.request.params.get('from')).toBe('2026-09-18T00:00:00Z');
      expect(req.request.params.get('page')).toBe('2');
      expect(req.request.params.get('pageSize')).toBe('20');
      expect(req.request.params.get('sort')).toBe('-startsAtUtc');
      expect(req.request.params.has('to')).toBe(false);
      expect(req.request.params.has('resourceId')).toBe(false);

      req.flush(page());
    });

    // The validator refuses a zone-less instant outright (an Unspecified
    // DateTime can only be interpreted by guessing a zone, and a silently
    // shifted filter window is a wrong answer with no error), so the caller has
    // to hand this method designated instants — asserted here so a future
    // caller building one by hand has a test saying what the shape is.
    it('passes from/to through with their zone designator intact', () => {
      firstValueFrom(service.list({ from: '2026-09-18T00:00:00Z', to: '2026-09-25T00:00:00Z' }));

      const req = httpMock.expectOne((r) => r.url === `${API}/bookings`);
      expect(req.request.params.get('from')).toBe('2026-09-18T00:00:00Z');
      expect(req.request.params.get('to')).toBe('2026-09-25T00:00:00Z');

      req.flush(page());
    });

    // **Replaces Phase 4's "never sends scope or userId".** Half of that is
    // still true and the other half is what Phase 6 is for: the approval queue
    // is the caller that asks for `scope=tenant`, so the parameter now goes out
    // when it was set. `userId` stays out of ListBookingsParams entirely — it
    // is TenantAdmin-only, nothing in WP-7 filters by one member, and sending
    // it alongside a non-Own scope is refused outright anyway.
    it('sends scope when the caller asked for it', () => {
      firstValueFrom(service.list({ scope: 'tenant', status: 'Pending' }));

      const req = httpMock.expectOne((r) => r.url === `${API}/bookings`);
      expect(req.request.params.get('scope')).toBe('tenant');
      expect(req.request.params.get('status')).toBe('Pending');
      expect(req.request.params.has('userId')).toBe(false);

      req.flush(page());
    });

    // The default still travels as an absence, not as a value. A member's own
    // calendar reads through this same method, and `scope=own` in its URL would
    // be this client restating a default the server already owns (decision
    // 0015) — the exact shape every other parameter here avoids.
    it('omits scope entirely when it was not set', () => {
      firstValueFrom(service.list({ status: 'Confirmed' }));

      const req = httpMock.expectOne((r) => r.url === `${API}/bookings`);
      expect(req.request.params.has('scope')).toBe(false);

      req.flush(page());
    });

    it('returns the paged envelope as the API sent it', async () => {
      const resultPromise = firstValueFrom(service.list());

      const body = page({ totalCount: 41, totalPages: 3, hasNextPage: true, page: 2 });
      httpMock.expectOne(`${API}/bookings`).flush(body);

      expect(await resultPromise).toEqual(body);
    });
  });

  describe('getById', () => {
    it('reads one booking and opts out of the global error toast', async () => {
      const resultPromise = firstValueFrom(service.getById('b1'));

      const req = httpMock.expectOne(`${API}/bookings/b1`);
      expect(req.request.method).toBe('GET');
      expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

      const body = detail();
      req.flush(body);

      expect(await resultPromise).toEqual(body);
    });

    // The three cancellation readings the detail screen has to tell apart. A
    // null actor beside a real reason is a blackout (Booking.CancelForBlackout
    // leaves it null on purpose), not missing data — so the type has to admit
    // it and the service has to pass it through untouched.
    it('passes a blackout cancellation through with its null actor intact', async () => {
      const resultPromise = firstValueFrom(service.getById('b1'));

      httpMock.expectOne(`${API}/bookings/b1`).flush(
        detail({
          status: 'Cancelled',
          cancelledByUserId: null,
          cancelledAtUtc: '2026-09-18T08:00:00Z',
          cancellationReason: 'Blackout: annual maintenance',
        }),
      );

      const result = await resultPromise;
      expect(result.cancelledByUserId).toBeNull();
      expect(result.cancellationReason).toBe('Blackout: annual maintenance');
    });

    // 404 covers another member's booking, another tenant's and a nonexistent
    // id alike — byte-identical by design (AC-4 within one tenant), which is
    // why the screen's not-found copy does not distinguish them.
    it('surfaces a 404 as an error rather than an empty result', async () => {
      const resultPromise = firstValueFrom(service.getById('someone-elses'));

      httpMock.expectOne(`${API}/bookings/someone-elses`).flush(
        { title: 'Not found.', status: 404, reasonCode: 'BookingNotFound', correlationId: 'c1' },
        { status: 404, statusText: 'Not Found' },
      );

      await expect(resultPromise).rejects.toMatchObject({
        status: 404,
        error: { reasonCode: 'BookingNotFound' },
      });
    });
  });

  describe('cancel', () => {
    it('posts to the cancel sub-route with the reason and skips the toast', async () => {
      const resultPromise = firstValueFrom(service.cancel('b1', { reason: 'Meeting moved' }));

      const req = httpMock.expectOne(`${API}/bookings/b1/cancel`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ reason: 'Meeting moved' });
      expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

      const body = cancelled({ cancellationReason: 'Meeting moved' });
      req.flush(body);

      expect(await resultPromise).toEqual(body);
    });

    // The body is optional server-side, but this client always sends one so
    // there is a single request shape to reason about — a null reason, not an
    // absent body.
    it('sends an explicit null reason rather than omitting the body', () => {
      firstValueFrom(service.cancel('b1', { reason: null }));

      const req = httpMock.expectOne(`${API}/bookings/b1/cancel`);
      expect(req.request.body).toEqual({ reason: null });

      req.flush(cancelled());
    });

    // The freed interval is the point of the reply: it names the span that just
    // became bookable again, which is what lets the screen update from the
    // response instead of blind-refetching.
    it('returns the freed interval and the actor who freed it', async () => {
      const resultPromise = firstValueFrom(service.cancel('b1', { reason: null }));

      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(
        cancelled({
          startsAtUtc: '2026-09-24T13:00:00Z',
          endsAtUtc: '2026-09-24T14:00:00Z',
          cancelledByUserId: 'admin-1',
        }),
      );

      const result = await resultPromise;
      expect(result.status).toBe('Cancelled');
      expect(result.startsAtUtc).toBe('2026-09-24T13:00:00Z');
      expect(result.cancelledByUserId).toBe('admin-1');
    });

    // Deliberately not idempotent: a second call is 422, never a second 200.
    // The screen has to render that as its own outcome, so the service must not
    // soften it into a success.
    it('surfaces a second cancellation as 422 BookingNotCancellable', async () => {
      const resultPromise = firstValueFrom(service.cancel('b1', { reason: null }));

      httpMock.expectOne(`${API}/bookings/b1/cancel`).flush(
        {
          title: 'The request was rejected by a rule.',
          status: 422,
          reasonCode: 'BookingNotCancellable',
          correlationId: 'c1',
        },
        { status: 422, statusText: 'Unprocessable Content' },
      );

      await expect(resultPromise).rejects.toMatchObject({
        status: 422,
        error: { reasonCode: 'BookingNotCancellable' },
      });
    });
  });

  describe('approve', () => {
    it('posts the note to /bookings/{id}/approve and opts out of the global error toast', async () => {
      const resultPromise = firstValueFrom(
        service.approve('b1', { note: 'Fine by me — the lab is free that morning.' }),
      );

      const req = httpMock.expectOne(`${API}/bookings/b1/approve`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ note: 'Fine by me — the lab is free that morning.' });
      expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

      const response = decided({ status: 'Confirmed' });
      req.flush(response);

      expect(await resultPromise).toEqual(response);
    });

    // The body is optional in full server-side, but this client always sends
    // one so there is a single request shape to test and reason about — the
    // same rule cancel() already follows.
    it('sends an explicit null note rather than an empty body', () => {
      firstValueFrom(service.approve('b1', { note: null }));

      const req = httpMock.expectOne(`${API}/bookings/b1/approve`);
      expect(req.request.body).toEqual({ note: null });

      req.flush(decided());
    });

    // AC-5, and the reason this endpoint is not interchangeable with reject:
    // dbo.ApproveBooking re-runs the capacity check under its lock, so the slot
    // can be gone by the time an approver gets to the request. The 409 has to
    // reach the caller intact — it is a different event from "already decided"
    // and step 5's dialect renders it as its own outcome.
    it('surfaces a 409 from the approval-time capacity re-check', async () => {
      const resultPromise = firstValueFrom(service.approve('b1', { note: null }));

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush({ reasonCode: 'SlotUnavailable' }, { status: 409, statusText: 'Conflict' });

      await expect(resultPromise).rejects.toMatchObject({
        status: 409,
        error: { reasonCode: 'SlotUnavailable' },
      });
    });

    // Not idempotent, deliberately: a second decision on the same booking is
    // 422 BookingNotPending, which is also what the losing approver sees when
    // two people decide at once. Asserted here so the no-retry rule in the
    // service's own header has a test behind it rather than only a comment.
    it('surfaces a 422 when the booking is no longer pending', async () => {
      const resultPromise = firstValueFrom(service.approve('b1', { note: null }));

      httpMock
        .expectOne(`${API}/bookings/b1/approve`)
        .flush(
          { reasonCode: 'BookingNotPending' },
          { status: 422, statusText: 'Unprocessable Entity' },
        );

      await expect(resultPromise).rejects.toMatchObject({
        status: 422,
        error: { reasonCode: 'BookingNotPending' },
      });
    });
  });

  describe('reject', () => {
    it('posts the note to /bookings/{id}/reject and opts out of the global error toast', async () => {
      const resultPromise = firstValueFrom(
        service.reject('b1', { note: 'The printer is out for maintenance that week.' }),
      );

      const req = httpMock.expectOne(`${API}/bookings/b1/reject`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ note: 'The printer is out for maintenance that week.' });
      expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

      const response = decided({ status: 'Rejected' });
      req.flush(response);

      expect(await resultPromise).toEqual(response);
    });

    it('sends an explicit null note rather than an empty body', () => {
      firstValueFrom(service.reject('b1', { note: null }));

      const req = httpMock.expectOne(`${API}/bookings/b1/reject`);
      expect(req.request.body).toEqual({ note: null });

      req.flush(decided({ status: 'Rejected' }));
    });

    // The two endpoints are not symmetrical and the client must not assume they
    // are: rejecting releases a claim rather than making one, so there is
    // nothing for the lock to refuse and no 409 to handle. Asserted as "hits
    // its own URL" rather than as an absence, since a missing status code
    // cannot be tested directly — what can be is that reject never goes
    // anywhere near the approve route.
    it('uses its own route, not approve with a flag', () => {
      firstValueFrom(service.reject('b1', { note: null }));

      httpMock.expectNone(`${API}/bookings/b1/approve`);
      httpMock.expectOne(`${API}/bookings/b1/reject`).flush(decided({ status: 'Rejected' }));
    });
  });
});

function decided(overrides: Partial<ApproveBookingResponse> = {}): ApproveBookingResponse {
  return {
    id: 'b1',
    status: 'Confirmed',
    decidedByUserId: 'approver-1',
    decidedAtUtc: '2026-09-21T11:00:00Z',
    ...overrides,
  };
}

function summary(overrides: Partial<BookingSummary> = {}): BookingSummary {
  return {
    id: 'b1',
    resourceId: 'r1',
    resourceName: 'Conference Room A',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: '2026-09-24T13:00:00Z',
    endsAtUtc: '2026-09-24T14:00:00Z',
    quantity: 1,
    title: null,
    status: 'Confirmed',
    createdAtUtc: '2026-09-20T08:00:00Z',
    ...overrides,
  };
}

function page(overrides: Partial<PagedResult<BookingSummary>> = {}): PagedResult<BookingSummary> {
  return {
    items: [summary()],
    page: 1,
    pageSize: 20,
    totalCount: 1,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
    ...overrides,
  };
}

function detail(overrides: Partial<BookingDetail> = {}): BookingDetail {
  return {
    ...summary(),
    checkedInAtUtc: null,
    cancelledByUserId: null,
    cancelledAtUtc: null,
    cancellationReason: null,
    createdAtUtc: '2026-09-17T09:00:00Z',
    updatedAtUtc: '2026-09-17T09:00:00Z',
    approval: null,
    ...overrides,
  };
}

function cancelled(overrides: Partial<CancelBookingResponse> = {}): CancelBookingResponse {
  return {
    id: 'b1',
    resourceId: 'r1',
    userId: 'u1',
    startsAtUtc: '2026-09-24T13:00:00Z',
    endsAtUtc: '2026-09-24T14:00:00Z',
    quantity: 1,
    title: null,
    status: 'Cancelled',
    cancelledByUserId: 'u1',
    cancelledAtUtc: '2026-09-18T10:00:00Z',
    cancellationReason: null,
    ...overrides,
  };
}
