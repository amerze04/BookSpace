import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { BookingsService } from './bookings.service';
import { CreateBookingRequest, CreateBookingResponse } from './booking.models';
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
});
