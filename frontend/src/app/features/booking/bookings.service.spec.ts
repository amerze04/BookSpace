import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { BookingsService } from './bookings.service';
import {
  BookingDetail,
  BookingSummary,
  CancelBookingResponse,
  CreateBookingRequest,
  CreateBookingResponse,
} from './booking.models';
import { PagedResult } from '../../core/http/paged-result';
import { SKIP_ERROR_TOAST } from '../../core/http/skip-error-toast';

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

    // Phase 4 is scope=Own only (wp7-plan.md's settled call 3) — and a plain
    // member's token is 400'd for sending either parameter, so this is not
    // merely unused surface but surface that would break the screen.
    it('never sends scope or userId — the widening belongs to Phase 6', () => {
      firstValueFrom(service.list({ status: 'Pending' }));

      const req = httpMock.expectOne((r) => r.url === `${API}/bookings`);
      expect(req.request.params.has('scope')).toBe(false);
      expect(req.request.params.has('userId')).toBe(false);

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
});

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
