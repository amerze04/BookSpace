import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { AvailabilityService } from './availability.service';
import { AvailabilityResponse } from './availability.models';

const API = 'http://localhost:5270';

function fakeResponse(overrides: Partial<AvailabilityResponse> = {}): AvailabilityResponse {
  return {
    resourceId: 'r1',
    timeZoneId: 'Europe/Sarajevo',
    fromLocalDate: '2026-09-20',
    toLocalDate: '2026-09-20',
    quantity: 1,
    isArchived: false,
    intervals: [{ startUtc: '2026-09-20T07:00:00Z', endUtc: '2026-09-20T15:00:00Z', remainingCapacity: 1 }],
    ...overrides,
  };
}

describe('AvailabilityService', () => {
  let service: AvailabilityService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AvailabilityService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('always sends from and to, and omits quantity when unset so the backend default applies', async () => {
    const resultPromise = firstValueFrom(
      service.get('r1', { from: '2026-09-20', to: '2026-09-20' }),
    );

    const req = httpMock.expectOne((r) => r.url === `${API}/resources/r1/availability`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('from')).toBe('2026-09-20');
    expect(req.request.params.get('to')).toBe('2026-09-20');
    expect(req.request.params.has('quantity')).toBe(false);

    const response = fakeResponse();
    req.flush(response);

    expect(await resultPromise).toEqual(response);
  });

  it('sends quantity when the caller provides one, for a pooled resource', async () => {
    const resultPromise = firstValueFrom(
      service.get('r2', { from: '2026-09-20', to: '2026-09-27', quantity: 3 }),
    );

    const req = httpMock.expectOne((r) => r.url === `${API}/resources/r2/availability`);
    expect(req.request.params.get('from')).toBe('2026-09-20');
    expect(req.request.params.get('to')).toBe('2026-09-27');
    expect(req.request.params.get('quantity')).toBe('3');

    req.flush(fakeResponse({ resourceId: 'r2', quantity: 3 }));
    await resultPromise;
  });

  it('surfaces the archived flag distinctly from an empty-but-open range', async () => {
    const resultPromise = firstValueFrom(
      service.get('r3', { from: '2026-09-20', to: '2026-09-20' }),
    );

    const req = httpMock.expectOne((r) => r.url === `${API}/resources/r3/availability`);
    const archivedResponse = fakeResponse({ resourceId: 'r3', isArchived: true, intervals: [] });
    req.flush(archivedResponse);

    expect(await resultPromise).toEqual(archivedResponse);
  });
});
