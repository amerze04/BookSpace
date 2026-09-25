import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute } from '@angular/router';
import { AdminUserDetailComponent } from '../components/admin-user-detail/admin-user-detail.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { UserDetail, UserRole } from '../models/users.models';
import { UserRejection } from '../rejection/user-rejection';

// User management phase 7. Roles, status, and the two confirmations.

const API = 'http://localhost:5270';

type TestableDetail = AdminUserDetailComponent & {
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  user: () => UserDetail | null;
  selectedRoles: () => ReadonlySet<UserRole>;
  rolesDirty: () => boolean;
  rolesEmpty: () => boolean;
  canSaveRoles: () => boolean;
  savingRoles: () => boolean;
  rolesSaved: () => boolean;
  rolesRejection: () => UserRejection | null;
  statusChanging: () => boolean;
  statusRejection: () => UserRejection | null;
  confirmingDeactivate: () => boolean;
  confirmingReactivate: () => boolean;
  toggleRole(role: UserRole): void;
  saveRoles(): void;
  startConfirmingDeactivate(): void;
  confirmDeactivate(): void;
  cancelConfirmingDeactivate(): void;
  startConfirmingReactivate(): void;
  confirmReactivate(): void;
  cancelConfirmingReactivate(): void;
  retryLoad(): void;
};

function problem(status: number, reasonCode: string) {
  return { status, title: 'The request was refused.', reasonCode };
}

function detail(overrides: Partial<UserDetail> = {}): UserDetail {
  return {
    id: 'u1',
    fullName: 'Member One',
    email: 'member1@acme.test',
    isActive: true,
    roles: ['Member'],
    createdAtUtc: '2026-09-01T09:00:00Z',
    updatedAtUtc: '2026-09-01T09:00:00Z',
    ...overrides,
  };
}

describe('AdminUserDetailComponent', () => {
  let httpMock: HttpTestingController;

  function create(): TestableDetail {
    TestBed.configureTestingModule({
      imports: [AdminUserDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'u1' } } } },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminUserDetailComponent).componentInstance as TestableDetail;
  }

  function createLoaded(user: UserDetail = detail()): TestableDetail {
    const component = create();
    httpMock.expectOne(`${API}/users/u1`).flush(user);
    return component;
  }

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      TestBed.inject(BreadcrumbService).setOverride(null);
      TestBed.resetTestingModule();
    }
  });

  // ---- Loading ----

  it('loads the person by id', () => {
    const component = createLoaded();

    expect(component.loading()).toBe(false);
    expect(component.user()?.fullName).toBe('Member One');
  });

  it('seeds the role checkboxes from what was loaded', () => {
    const component = createLoaded(detail({ roles: ['Approver', 'Member'] }));

    expect(component.selectedRoles()).toEqual(new Set(['Approver', 'Member']));
    expect(component.rolesDirty()).toBe(false);
  });

  it('puts the loaded name on the breadcrumb', () => {
    createLoaded();

    expect(TestBed.inject(BreadcrumbService).override()).toBe('Member One');
  });

  it('shows a not-found state for a 404', () => {
    const component = create();
    httpMock.expectOne(`${API}/users/u1`).flush(problem(404, 'UserNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
    expect(component.loading()).toBe(false);
  });

  it('shows a retryable error for anything else', () => {
    const component = create();
    httpMock.expectOne(`${API}/users/u1`).flush({}, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);

    component.retryLoad();
    httpMock.expectOne(`${API}/users/u1`).flush(detail());
    expect(component.loadError()).toBe(false);
  });

  // ---- Roles ----

  it('tracks a toggle as a change against what was loaded', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));

    component.toggleRole('Approver');

    expect(component.selectedRoles()).toEqual(new Set(['Member', 'Approver']));
    expect(component.rolesDirty()).toBe(true);
    expect(component.canSaveRoles()).toBe(true);
  });

  // The client-side echo of the validator's own rule: a user cannot be left
  // with no roles, because TenantMember needs only the orgId claim.
  it('refuses to save an empty role set', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));

    component.toggleRole('Member');

    expect(component.rolesEmpty()).toBe(true);
    expect(component.canSaveRoles()).toBe(false);
  });

  it('saves the whole set and reseeds from the response', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));
    component.toggleRole('Approver');

    component.saveRoles();
    const req = httpMock.expectOne(`${API}/users/u1/roles`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ roles: expect.arrayContaining(['Member', 'Approver']) });

    req.flush({
      id: 'u1',
      email: 'member1@acme.test',
      fullName: 'Member One',
      isActive: true,
      roles: ['Approver', 'Member'],
      updatedAtUtc: '2026-09-25T10:00:00Z',
    });

    expect(component.rolesSaved()).toBe(true);
    expect(component.rolesDirty()).toBe(false);
    expect(component.savingRoles()).toBe(false);
  });

  // Decision `0031`, rendered as something to act on rather than a wall.
  it('renders LastTenantAdmin against the roles save', () => {
    const component = createLoaded(detail({ roles: ['TenantAdmin'] }));
    component.toggleRole('TenantAdmin');
    component.toggleRole('Member');

    component.saveRoles();
    httpMock.expectOne(`${API}/users/u1/roles`).flush(problem(422, 'LastTenantAdmin'), {
      status: 422,
      statusText: 'Unprocessable Entity',
    });

    expect(component.rolesRejection()?.formMessage).toContain('only active administrator');
    expect(component.savingRoles()).toBe(false);
  });

  it('switches to the not-found state if the account left this reach mid-edit', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));
    component.toggleRole('Approver');

    component.saveRoles();
    httpMock.expectOne(`${API}/users/u1/roles`).flush(problem(404, 'UserNotFound'), {
      status: 404,
      statusText: 'Not Found',
    });

    expect(component.notFound()).toBe(true);
  });

  // ---- Deactivate ----

  it('deactivates after confirming, and updates the badge from the response', () => {
    const component = createLoaded(detail({ isActive: true }));

    component.startConfirmingDeactivate();
    expect(component.confirmingDeactivate()).toBe(true);

    component.confirmDeactivate();
    const req = httpMock.expectOne(`${API}/users/u1/deactivate`);
    expect(req.request.method).toBe('POST');
    req.flush({
      id: 'u1',
      email: 'member1@acme.test',
      fullName: 'Member One',
      isActive: false,
      roles: ['Member'],
      updatedAtUtc: '2026-09-25T10:00:00Z',
    });

    expect(component.user()?.isActive).toBe(false);
    expect(component.confirmingDeactivate()).toBe(false);
    expect(component.statusChanging()).toBe(false);
  });

  it('cancelling the deactivate confirmation makes no request', () => {
    const component = createLoaded(detail({ isActive: true }));

    component.startConfirmingDeactivate();
    component.cancelConfirmingDeactivate();

    expect(component.confirmingDeactivate()).toBe(false);
    httpMock.verify();
  });

  it('renders LastTenantAdmin against the deactivate confirmation', () => {
    const component = createLoaded(detail({ isActive: true, roles: ['TenantAdmin'] }));

    component.startConfirmingDeactivate();
    component.confirmDeactivate();
    httpMock.expectOne(`${API}/users/u1/deactivate`).flush(problem(422, 'LastTenantAdmin'), {
      status: 422,
      statusText: 'Unprocessable Entity',
    });

    expect(component.statusRejection()?.formMessage).toContain('only active administrator');
    // The refusal leaves the account exactly as it was — still active, and
    // still on the confirmation panel rather than silently dismissed.
    expect(component.user()?.isActive).toBe(true);
  });

  // ---- Reactivate ----

  it('reactivates after confirming', () => {
    const component = createLoaded(detail({ isActive: false }));

    component.startConfirmingReactivate();
    component.confirmReactivate();
    const req = httpMock.expectOne(`${API}/users/u1/reactivate`);
    expect(req.request.method).toBe('POST');
    req.flush({
      id: 'u1',
      email: 'member1@acme.test',
      fullName: 'Member One',
      isActive: true,
      roles: ['Member'],
      updatedAtUtc: '2026-09-25T10:00:00Z',
    });

    expect(component.user()?.isActive).toBe(true);
    expect(component.confirmingReactivate()).toBe(false);
  });

  it('cancelling the reactivate confirmation makes no request', () => {
    const component = createLoaded(detail({ isActive: false }));

    component.startConfirmingReactivate();
    component.cancelConfirmingReactivate();

    expect(component.confirmingReactivate()).toBe(false);
    httpMock.verify();
  });
});
