import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { ResourcesService } from '../services/resources.service';
import { PagedResult } from '../../../core/http/paged-result';
import { ResourceDetail, ResourceSummary } from '../models/resources.models';

const API = 'http://localhost:5270';

function fakeSummary(overrides: Partial<ResourceSummary> = {}): ResourceSummary {
  return {
    id: 'r1',
    name: 'Conference Room A',
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'Europe/Sarajevo',
    requiresApproval: true,
    isArchived: false,
    ...overrides,
  };
}

function fakePage(items: ResourceSummary[]): PagedResult<ResourceSummary> {
  return {
    items,
    page: 1,
    pageSize: 20,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
  };
}

describe('ResourcesService', () => {
  let service: ResourcesService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ResourcesService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('sends no query params at all when list() is called with none, so the backend defaults apply', async () => {
    const resultPromise = firstValueFrom(service.list());

    const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(req.request.params.keys()).toEqual([]);

    const page = fakePage([fakeSummary()]);
    req.flush(page);

    expect(await resultPromise).toEqual(page);
  });

  it('sends every provided param, including includeArchived=false explicitly', async () => {
    const resultPromise = firstValueFrom(
      service.list({ page: 2, pageSize: 100, sort: '-capacity', includeArchived: false, type: 'Room' }),
    );

    const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('100');
    expect(req.request.params.get('sort')).toBe('-capacity');
    // The false case is the one a naive `if (value)` check would drop —
    // asserted explicitly so that regression can't creep back in silently.
    expect(req.request.params.get('includeArchived')).toBe('false');
    expect(req.request.params.get('type')).toBe('Room');

    req.flush(fakePage([]));
    await resultPromise;
  });

  it('omits includeArchived from the query string when it is left unset', async () => {
    const resultPromise = firstValueFrom(service.list({ includeArchived: undefined }));

    const req = httpMock.expectOne((r) => r.url === `${API}/resources`);
    expect(req.request.params.has('includeArchived')).toBe(false);

    req.flush(fakePage([]));
    await resultPromise;
  });

  it('fetches one resource by id', async () => {
    const detail: ResourceDetail = {
      id: 'r1',
      name: 'Conference Room A',
      description: 'Spacious 8-person meeting room.',
      resourceType: 'Room',
      capacity: 1,
      timeZoneId: 'Europe/Sarajevo',
      requiresApproval: true,
      minDurationMinutes: 30,
      maxDurationMinutes: 240,
      isArchived: false,
      createdAtUtc: '2026-08-21T00:00:00Z',
      updatedAtUtc: '2026-08-21T00:00:00Z',
      availabilityWindows: [{ id: 'w1', weekday: 'Monday', opensAt: '08:00:00', closesAt: '18:00:00' }],
      approvers: [{ userId: 'u1', fullName: 'Facilities Team' }],
    };

    const resultPromise = firstValueFrom(service.getById('r1'));

    const req = httpMock.expectOne(`${API}/resources/r1`);
    expect(req.request.method).toBe('GET');
    req.flush(detail);

    expect(await resultPromise).toEqual(detail);
  });
});
