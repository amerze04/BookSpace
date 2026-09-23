import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { UsersService } from '../services/users.service';
import { SKIP_ERROR_TOAST } from '../../../core/http/skip-error-toast';

// Admin console phase 7, coverage sweep. The other file the enumeration found:
// `GET /users` is the one endpoint this console owns outright (phase 1 added it
// for the approvers picker) and its client had no spec, so the only thing
// asserting the wire shape was a component spec that mocks it.

const API = 'http://localhost:5270';

function page(items: unknown[] = []) {
  return {
    items,
    page: 1,
    pageSize: 50,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
  };
}

describe('UsersService', () => {
  let service: UsersService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UsersService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // Not nested under a resource, unlike blackout periods: eligibility is a
  // property of the tenant's people, not of the room being configured.
  it('reads the eligible-approver set from the top-level route', async () => {
    const result = firstValueFrom(service.list());

    const req = httpMock.expectOne(`${API}/users`);
    expect(req.request.method).toBe('GET');

    const body = page([
      { id: 'u9', fullName: 'Dana Admin', email: 'dana@acme.test', roles: ['TenantAdmin'] },
    ]);
    req.flush(body);

    expect(await result).toEqual(body);
  });

  // The approvers screen renders its own inline state for a failed load, and a
  // global toast on top of it would be noise over the one explanation that
  // helps. Every admin console call opts out the same way.
  it('opts out of the global error toast', () => {
    firstValueFrom(service.list());

    const req = httpMock.expectOne(`${API}/users`);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    req.flush(page());
  });

  // Decision `0015`: an omitted filter is genuinely absent from the URL, so the
  // backend's own documented default applies rather than a second copy of it
  // living in this file and drifting from it.
  it('sends no query string when no filter was set', () => {
    firstValueFrom(service.list());

    const req = httpMock.expectOne(`${API}/users`);
    expect(req.request.params.keys()).toEqual([]);

    req.flush(page());
  });

  it('sends every filter it was given, and nothing it was not', () => {
    firstValueFrom(service.list({ page: 2, pageSize: 50, search: 'dana', sort: 'fullName' }));

    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    expect(req.request.params.get('search')).toBe('dana');
    expect(req.request.params.get('sort')).toBe('fullName');

    req.flush(page());
  });

  // Checked against `undefined` rather than truthiness. Page 0 is not a legal
  // page — but dropping it silently would turn the server's refusal into page 1
  // of somebody else's results, which is the failure mode phase 1 spent a whole
  // repository change avoiding.
  it('sends a zero page rather than dropping it, so the server can refuse it', () => {
    firstValueFrom(service.list({ page: 0, pageSize: 0 }));

    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.params.get('page')).toBe('0');
    expect(req.request.params.get('pageSize')).toBe('0');

    req.flush(page());
  });

  // An empty search is a real value, not an absent one: it is how the picker
  // returns to showing everybody after a term is cleared, and it is also what
  // `strandedApprovers` requires before it trusts an absence (phase 5).
  it('sends an empty search term rather than treating it as unset', () => {
    firstValueFrom(service.list({ search: '' }));

    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.params.has('search')).toBe(true);
    expect(req.request.params.get('search')).toBe('');

    req.flush(page());
  });

  // **There is no "all users" mode, and that is the point of the endpoint.**
  // The route is broader than the answer — it returns decision `0018`'s eligible
  // set and nothing widens it — so no caller can ask this service for a tenant
  // directory by passing something extra.
  it('exposes no parameter that would widen the answer beyond the eligible set', () => {
    firstValueFrom(service.list({ search: 'anybody' }));
    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.params.keys().sort()).toEqual(['search']);

    req.flush(page());
  });
});
