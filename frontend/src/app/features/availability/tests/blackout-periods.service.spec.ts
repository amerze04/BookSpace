import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { BlackoutPeriodsService } from '../services/blackout-periods.service';
import { SKIP_ERROR_TOAST } from '../../../core/http/skip-error-toast';

// WP-7 Phase 7 step 2. The last API service in the app without a spec — missed
// by the first coverage audit, which compared names by eye and did not notice
// this one sitting under `availability/services/` rather than beside the others.
// The systematic sweep found it, which is the argument for doing the sweep.
const API = 'http://localhost:5270';

function page(items: unknown[] = []) {
  return {
    items,
    page: 1,
    pageSize: 100,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
  };
}

describe('BlackoutPeriodsService', () => {
  let service: BlackoutPeriodsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(BlackoutPeriodsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // Nested under the resource, unlike bookings: a blackout has no meaning apart
  // from the resource it blocks, which is why the backend nests this route and
  // not `/bookings`.
  it('reads a resource own blackout periods from its nested route', async () => {
    const result = firstValueFrom(service.list('r1'));

    const req = httpMock.expectOne(`${API}/resources/r1/blackout-periods`);
    expect(req.request.method).toBe('GET');

    const body = page([{ id: 'bp1', reason: 'Maintenance' }]);
    req.flush(body);

    expect(await result).toEqual(body);
  });

  // **Its failure is deliberately silent on the availability screen** — the
  // blackout fetch only supplies a *reason* for gaps the availability response
  // has already excluded, so losing it costs a label rather than correctness.
  // The opt-out is what keeps a generic toast from appearing over a screen that
  // is still perfectly usable.
  it('opts out of the global error toast', () => {
    firstValueFrom(service.list('r1'));

    const req = httpMock.expectOne(`${API}/resources/r1/blackout-periods`);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    req.flush(page());
  });

  // The project-wide rule (decision `0015`): an omitted filter is genuinely
  // absent from the URL, so the backend's own documented default applies rather
  // than a second copy of it invented here.
  it('sends no query string when no filter was set', () => {
    firstValueFrom(service.list('r1'));

    const req = httpMock.expectOne(`${API}/resources/r1/blackout-periods`);
    expect(req.request.params.keys()).toEqual([]);

    req.flush(page());
  });

  it('sends every filter it was given, and nothing it was not', () => {
    firstValueFrom(
      service.list('r1', {
        from: '2026-09-21T00:00:00Z',
        to: '2026-09-30T00:00:00Z',
        pageSize: 100,
      }),
    );

    const req = httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`);
    expect(req.request.params.get('from')).toBe('2026-09-21T00:00:00Z');
    expect(req.request.params.get('to')).toBe('2026-09-30T00:00:00Z');
    expect(req.request.params.get('pageSize')).toBe('100');
    expect(req.request.params.has('page')).toBe(false);
    expect(req.request.params.has('sort')).toBe(false);

    req.flush(page());
  });

  // `page`/`pageSize` are checked against `undefined` rather than truthiness,
  // which matters for page 0 — not a legal page, but a value that a truthiness
  // check would silently drop instead of letting the server refuse it.
  it('sends a zero page rather than dropping it, so the server can refuse it', () => {
    firstValueFrom(service.list('r1', { page: 0 }));

    const req = httpMock.expectOne((r) => r.url === `${API}/resources/r1/blackout-periods`);
    expect(req.request.params.get('page')).toBe('0');

    req.flush(page());
  });
});
