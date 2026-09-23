import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { ResourcesService } from '../services/resources.service';
import { PagedResult } from '../../../core/http/paged-result';
import {
  CreateResourceRequest,
  ResourceDetail,
  ResourceSummary,
  UpdateResourceRequest,
} from '../models/resources.models';
import { SKIP_ERROR_TOAST } from '../../../core/http/skip-error-toast';

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

  // ---- Writes (admin console phase 3) ----

  it('creates through POST /resources and skips the global toast', async () => {
    const request: CreateResourceRequest = {
      name: '3D Printer',
      description: null,
      resourceType: 'Equipment',
      capacity: 2,
      timeZoneId: 'UTC',
      requiresApproval: false,
      minDurationMinutes: null,
      maxDurationMinutes: null,
    };

    const resultPromise = firstValueFrom(service.create(request));

    const req = httpMock.expectOne(`${API}/resources`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    // The admin form renders every one of these failures inline, worded by
    // `resource-rejection.ts`, so a generic toast on top would be noise over
    // the one explanation that actually helps.
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    req.flush({ ...request, id: 'new-1', isArchived: false, createdAtUtc: 'x', updatedAtUtc: 'x' });

    expect((await resultPromise).id).toBe('new-1');
  });

  // **PUT, not PATCH.** The body is a full representation and an omitted
  // nullable field means cleared (decision `0015`), so the request has to carry
  // every mutable field even when only one of them changed.
  it('updates through PUT /resources/{id} with the whole representation', async () => {
    const request: UpdateResourceRequest = {
      name: 'Conference Room A',
      description: 'Main conference room',
      resourceType: 'Room',
      capacity: 1,
      timeZoneId: 'Europe/Warsaw',
      requiresApproval: false,
      minDurationMinutes: 30,
      maxDurationMinutes: 240,
    };

    const resultPromise = firstValueFrom(service.update('r1', request));

    const req = httpMock.expectOne(`${API}/resources/r1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(request);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    req.flush({ ...request, id: 'r1', isArchived: false, createdAtUtc: 'x', updatedAtUtc: 'y', timeZoneChange: null });

    expect((await resultPromise).timeZoneChange).toBeNull();
  });

  // **POST to the transition, never DELETE /resources/{id}.** Nothing in this
  // system is deleted (CLAUDE.md §4.5), and a DELETE that silently meant
  // "archive, irreversibly" would invite a client to assume the row was gone.
  it('archives through POST /resources/{id}/archive, not DELETE', async () => {
    const resultPromise = firstValueFrom(service.archive('r1'));

    const req = httpMock.expectOne(`${API}/resources/r1/archive`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});

    req.flush({
      id: 'r1',
      name: 'Conference Room A',
      description: null,
      resourceType: 'Room',
      capacity: 1,
      timeZoneId: 'UTC',
      requiresApproval: false,
      minDurationMinutes: null,
      maxDurationMinutes: null,
      isArchived: true,
      createdAtUtc: 'x',
      updatedAtUtc: 'y',
    });

    expect((await resultPromise).isArchived).toBe(true);
  });
});
