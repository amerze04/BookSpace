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

  // **`list` cannot be talked into the wider answer.** It used to be that the
  // endpoint had no wider answer at all; user management phase 4 gave it one,
  // behind `scope`, and this is what keeps the picker's call out of it — a
  // caller passing something extra still gets decision `0018`'s eligible set,
  // because `list` sends no scope and an omitted scope is the narrow one.
  it('never sends a scope, whatever it is passed', () => {
    firstValueFrom(service.list({ search: 'anybody' }));
    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.params.keys().sort()).toEqual(['search']);
    expect(req.request.params.has('scope')).toBe(false);

    req.flush(page());
  });

  // ---- User management phase 6 ----

  // The directory's read. Same route, one parameter apart — and that parameter
  // is the whole difference between "everyone here" and "the two people who can
  // approve things".
  it('asks for every user in the tenant through listDirectory', async () => {
    const result = firstValueFrom(service.listDirectory());

    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('scope')).toBe('All');

    const body = page([
      { id: 'u1', fullName: 'Member One', email: 'member1@acme.test', isActive: true, roles: ['Member'] },
    ]);
    req.flush(body);

    expect(await result).toEqual(body);
  });

  it('carries paging and search into the directory read alongside the scope', () => {
    firstValueFrom(service.listDirectory({ page: 2, pageSize: 50, search: 'ada' }));

    const req = httpMock.expectOne((r) => r.url === `${API}/users`);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    expect(req.request.params.get('search')).toBe('ada');
    expect(req.request.params.get('scope')).toBe('All');

    req.flush(page());
  });

  it('posts a new user to the top-level route', async () => {
    const created = {
      id: 'u9',
      email: 'ada@acme.test',
      fullName: 'Ada Lovelace',
      isActive: true,
      roles: ['Member'],
      createdAtUtc: '2026-09-25T09:00:00Z',
      activationLink: 'https://bookspace.test/activate?token=abc',
      activationLinkExpiresAtUtc: '2026-10-02T09:00:00Z',
      invitationEmailSent: true,
    };
    const result = firstValueFrom(service.create({ email: 'ada@acme.test', fullName: 'Ada Lovelace' }));

    const req = httpMock.expectOne(`${API}/users`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'ada@acme.test', fullName: 'Ada Lovelace' });
    req.flush(created);

    expect(await result).toEqual(created);
  });

  // The create screen renders its own inline refusal — a taken address belongs
  // under the email control, not in a toast that says nothing useful.
  it('skips the global error toast for both new calls', () => {
    firstValueFrom(service.listDirectory()).catch(() => undefined);
    expect(httpMock.expectOne((r) => r.url === `${API}/users`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);
    httpMock.verify();

    firstValueFrom(service.create({ email: 'a@b.test', fullName: 'A' })).catch(() => undefined);
    expect(httpMock.expectOne(`${API}/users`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);
  });

  // ---- User management phase 7 ----

  it('reads a single person by id', async () => {
    const detail = {
      id: 'u1',
      fullName: 'Member One',
      email: 'member1@acme.test',
      isActive: true,
      roles: ['Member'],
      createdAtUtc: '2026-09-01T09:00:00Z',
      updatedAtUtc: '2026-09-01T09:00:00Z',
    };
    const result = firstValueFrom(service.getById('u1'));

    const req = httpMock.expectOne(`${API}/users/u1`);
    expect(req.request.method).toBe('GET');
    req.flush(detail);

    expect(await result).toEqual(detail);
  });

  it('posts a deactivation with no body', () => {
    firstValueFrom(service.deactivate('u1')).catch(() => undefined);

    const req = httpMock.expectOne(`${API}/users/u1/deactivate`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();

    req.flush({});
  });

  it('posts a reactivation with no body', () => {
    firstValueFrom(service.reactivate('u1')).catch(() => undefined);

    const req = httpMock.expectOne(`${API}/users/u1/reactivate`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();

    req.flush({});
  });

  it('replaces the whole role set in one PUT', () => {
    firstValueFrom(service.replaceRoles('u1', { roles: ['Approver', 'Member'] })).catch(() => undefined);

    const req = httpMock.expectOne(`${API}/users/u1/roles`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ roles: ['Approver', 'Member'] });

    req.flush({});
  });

  it('skips the global error toast for all four detail-screen calls', () => {
    firstValueFrom(service.getById('u1')).catch(() => undefined);
    expect(httpMock.expectOne(`${API}/users/u1`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    firstValueFrom(service.deactivate('u1')).catch(() => undefined);
    expect(httpMock.expectOne(`${API}/users/u1/deactivate`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    firstValueFrom(service.reactivate('u1')).catch(() => undefined);
    expect(httpMock.expectOne(`${API}/users/u1/reactivate`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    firstValueFrom(service.replaceRoles('u1', { roles: ['Member'] })).catch(() => undefined);
    expect(httpMock.expectOne(`${API}/users/u1/roles`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);
  });

  // ---- Hardening pass, 2026-09-25 (finding 3) ----

  it('posts a reissue request with no body', () => {
    firstValueFrom(service.reissueInvitation('u1')).catch(() => undefined);

    const req = httpMock.expectOne(`${API}/users/u1/invitation`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();

    req.flush({});
  });

  it('skips the global error toast for the reissue call', () => {
    firstValueFrom(service.reissueInvitation('u1')).catch(() => undefined);

    expect(httpMock.expectOne(`${API}/users/u1/invitation`).request.context.get(SKIP_ERROR_TOAST)).toBe(true);
  });
});
