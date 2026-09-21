import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { RecurrenceRulesService } from '../services/recurrence-rules.service';
import {
  CancelRecurrenceSeriesResponse,
  CreateRecurrenceSeriesRequest,
  CreateRecurrenceSeriesResponse,
  NoOccurrencesCreatedProblem,
} from '../models/recurrence.models';
import { SKIP_ERROR_TOAST } from '../../../core/http/skip-error-toast';

const API = 'http://localhost:5270';

// The occurrence-count-bound arm of the end condition. The endDate arm is
// built explicitly in its own test, since the whole point of the union is
// that the two cannot be spread over one another.
function countBoundRequest(
  overrides: Partial<Omit<CreateRecurrenceSeriesRequest, 'endDate'>> = {},
): CreateRecurrenceSeriesRequest {
  return {
    resourceId: 'r1',
    frequency: 'Weekly',
    intervalValue: 1,
    localStartTime: '09:00:00',
    localEndTime: '10:00:00',
    startDate: '2026-09-21',
    occurrenceCount: 4,
    quantity: 1,
    title: null,
    ...overrides,
  };
}

function response(
  overrides: Partial<CreateRecurrenceSeriesResponse> = {},
): CreateRecurrenceSeriesResponse {
  return {
    recurrenceRuleId: 'rr1',
    occurrences: [
      { occurrenceDate: '2026-09-21', status: 'Created', bookingId: 'b1', reasonCode: null },
      {
        occurrenceDate: '2026-09-28',
        status: 'Refused',
        bookingId: null,
        reasonCode: 'SlotUnavailable',
      },
    ],
    ...overrides,
  };
}

describe('RecurrenceRulesService', () => {
  let service: RecurrenceRulesService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RecurrenceRulesService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('posts local wall-clock times to /recurrence-rules and opts out of the global error toast', async () => {
    const body = countBoundRequest({ title: 'Standup' });
    const resultPromise = firstValueFrom(service.create(body, 'key-1'));

    const req = httpMock.expectOne(`${API}/recurrence-rules`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    // No timeZoneId field: decision 0003 puts the series in the resource's own
    // zone, so there is deliberately nothing here for a client to name.
    expect(Object.keys(req.request.body as object)).not.toContain('timeZoneId');
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    const body201 = response();
    req.flush(body201, { status: 201, statusText: 'Created' });

    expect(await resultPromise).toEqual(body201);
  });

  it('sends the idempotency key as an Idempotency-Key header, not a body field', () => {
    firstValueFrom(service.create(countBoundRequest(), 'attempt-7'));

    const req = httpMock.expectOne(`${API}/recurrence-rules`);
    expect(req.request.headers.get('Idempotency-Key')).toBe('attempt-7');
    expect(Object.keys(req.request.body as object)).not.toContain('idempotencyKey');

    req.flush(response(), { status: 201, statusText: 'Created' });
  });

  // CK_RecurrenceRules_EndCondition on the wire: the serialized body carries
  // exactly one of the two fields. Asserted against JSON.parse(JSON.stringify(...))
  // rather than the request object itself, because that is what the wire
  // actually receives — an explicitly-undefined key is dropped there, and a
  // null one would not be.
  it('serializes an occurrence-count-bound series with occurrenceCount and no endDate', () => {
    firstValueFrom(service.create(countBoundRequest({ occurrenceCount: 12 }), 'key-1'));

    const req = httpMock.expectOne(`${API}/recurrence-rules`);
    const wire = JSON.parse(JSON.stringify(req.request.body)) as Record<string, unknown>;
    expect(wire['occurrenceCount']).toBe(12);
    expect('endDate' in wire).toBe(false);

    req.flush(response(), { status: 201, statusText: 'Created' });
  });

  it('serializes an end-date-bound series with endDate and no occurrenceCount', () => {
    const body: CreateRecurrenceSeriesRequest = {
      resourceId: 'r1',
      frequency: 'Monthly',
      intervalValue: 2,
      localStartTime: '14:00:00',
      localEndTime: '15:30:00',
      startDate: '2026-10-01',
      endDate: '2027-04-01',
      occurrenceCount: undefined,
      quantity: 1,
      title: null,
    };
    firstValueFrom(service.create(body, 'key-2'));

    const req = httpMock.expectOne(`${API}/recurrence-rules`);
    const wire = JSON.parse(JSON.stringify(req.request.body)) as Record<string, unknown>;
    expect(wire['endDate']).toBe('2027-04-01');
    expect('occurrenceCount' in wire).toBe(false);

    req.flush(response({ occurrences: [] }), { status: 201, statusText: 'Created' });
  });

  it('passes a pooled quantity and a DST-skipped occurrence through unchanged', async () => {
    const resultPromise = firstValueFrom(
      service.create(countBoundRequest({ quantity: 3, frequency: 'Daily' }), 'key-3'),
    );

    const req = httpMock.expectOne(`${API}/recurrence-rules`);
    expect((req.request.body as CreateRecurrenceSeriesRequest).quantity).toBe(3);

    const body201 = response({
      occurrences: [
        { occurrenceDate: '2027-03-13', status: 'Created', bookingId: 'b1', reasonCode: null },
        {
          occurrenceDate: '2027-03-14',
          status: 'SkippedSpringForwardGap',
          bookingId: null,
          reasonCode: null,
        },
      ],
    });
    req.flush(body201, { status: 201, statusText: 'Created' });

    expect(await resultPromise).toEqual(body201);
  });

  // The all-refused case: the identical breakdown, on a 422, as a top-level
  // `occurrences` key beside reasonCode — shape confirmed against the running
  // API (see NoOccurrencesCreatedProblem). This test is what keeps the typed
  // shape honest; step 7's renderer is what consumes it.
  it('surfaces an all-refused series as a 422 carrying the same occurrence breakdown', async () => {
    const resultPromise = firstValueFrom(service.create(countBoundRequest(), 'key-4'));

    const req = httpMock.expectOne(`${API}/recurrence-rules`);
    const problem: NoOccurrencesCreatedProblem = {
      title: 'The request was rejected by a rule.',
      status: 422,
      reasonCode: 'NoOccurrencesCreated',
      correlationId: 'c1',
      occurrences: [
        {
          occurrenceDate: '2026-09-21',
          status: 'Refused',
          bookingId: null,
          reasonCode: 'OutsideAvailability',
        },
        {
          occurrenceDate: '2026-09-28',
          status: 'Refused',
          bookingId: null,
          reasonCode: 'OutsideAvailability',
        },
      ],
    };
    req.flush(problem, { status: 422, statusText: 'Unprocessable Content' });

    await expect(resultPromise).rejects.toMatchObject({
      status: 422,
      error: {
        reasonCode: 'NoOccurrencesCreated',
        occurrences: [{ reasonCode: 'OutsideAvailability' }, { reasonCode: 'OutsideAvailability' }],
      },
    });
  });

  describe('cancel', () => {
    it('posts to the series cancel sub-route and skips the global toast', async () => {
      const resultPromise = firstValueFrom(
        service.cancel('rr1', { reason: 'Project finished early' }),
      );

      const req = httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ reason: 'Project finished early' });
      expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

      const body: CancelRecurrenceSeriesResponse = {
        recurrenceRuleId: 'rr1',
        cancelledByUserId: 'u1',
        cancelledAtUtc: '2026-09-18T10:00:00Z',
        cancelledBookingIds: ['b2', 'b3'],
      };
      req.flush(body);

      expect(await resultPromise).toEqual(body);
    });

    // Unlike create, this endpoint takes no key — it inherits the single
    // cancel's non-idempotency one level up, so there is nothing for a key to
    // resolve to. Asserted rather than assumed: the backend ignores an unknown
    // header, so a client that believed it had one would then feel free to
    // retry, which is exactly what must not happen here.
    it('sends no Idempotency-Key — only the create endpoint has one', () => {
      firstValueFrom(service.cancel('rr1', { reason: null }));

      const req = httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`);
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      expect(req.request.body).toEqual({ reason: null });

      req.flush({
        recurrenceRuleId: 'rr1',
        cancelledByUserId: 'u1',
        cancelledAtUtc: '2026-09-18T10:00:00Z',
        cancelledBookingIds: [],
      } satisfies CancelRecurrenceSeriesResponse);
    });

    // Only occurrences with EndsAtUtc > now are cancelled, so a series whose
    // every occurrence is in the past frees nothing — a legitimate 200 with an
    // empty list, not a failure. The screen reports what was actually freed
    // from this list rather than a bare success, so an empty one has to survive
    // the round trip as an empty one.
    it('passes an empty cancelledBookingIds through as a success', async () => {
      const resultPromise = firstValueFrom(service.cancel('rr1', { reason: null }));

      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).flush({
        recurrenceRuleId: 'rr1',
        cancelledByUserId: 'u1',
        cancelledAtUtc: '2026-09-18T10:00:00Z',
        cancelledBookingIds: [],
      } satisfies CancelRecurrenceSeriesResponse);

      expect((await resultPromise).cancelledBookingIds).toEqual([]);
    });

    it('surfaces a refusal as an error rather than swallowing it', async () => {
      const resultPromise = firstValueFrom(service.cancel('rr1', { reason: null }));

      httpMock.expectOne(`${API}/recurrence-rules/rr1/cancel`).flush(
        {
          title: 'The request was rejected by a rule.',
          status: 422,
          reasonCode: 'RecurrenceRuleNotCancellable',
          correlationId: 'c1',
        },
        { status: 422, statusText: 'Unprocessable Content' },
      );

      await expect(resultPromise).rejects.toMatchObject({
        status: 422,
        error: { reasonCode: 'RecurrenceRuleNotCancellable' },
      });
    });
  });
});
