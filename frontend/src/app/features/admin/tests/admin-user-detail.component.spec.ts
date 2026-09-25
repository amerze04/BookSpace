import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AdminUserDetailComponent } from '../components/admin-user-detail/admin-user-detail.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { UserDetail, UserRole } from '../models/users.models';
import { UserRejection } from '../rejection/user-rejection';

// User management phase 7. Roles, status, the confirmations, and
// (2026-09-25 hardening pass) resending an invitation, the route-id fix, and
// the Member-mandatory invariant.

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
  canResendInvitation: () => boolean;
  resending: () => boolean;
  resendResult: () => { invitationEmailSent: boolean } | null;
  resendRejection: () => UserRejection | null;
  toggleRole(role: UserRole): void;
  saveRoles(): void;
  startConfirmingDeactivate(): void;
  confirmDeactivate(): void;
  cancelConfirmingDeactivate(): void;
  startConfirmingReactivate(): void;
  confirmReactivate(): void;
  cancelConfirmingReactivate(): void;
  resendInvitation(): void;
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
    isActivated: false,
    ...overrides,
  };
}

function writeResult(overrides: Record<string, unknown> = {}) {
  return {
    id: 'u1',
    email: 'member1@acme.test',
    fullName: 'Member One',
    isActive: true,
    roles: ['Member'],
    updatedAtUtc: '2026-09-25T10:00:00Z',
    ...overrides,
  };
}

describe('AdminUserDetailComponent', () => {
  let httpMock: HttpTestingController;
  let paramMap$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  // A BehaviorSubject, not a bare route stub, so a test can push a new id
  // through it and prove the component reacts — hardening pass, 2026-09-25,
  // finding 5 (the same shape ResourceDetailComponent's own spec uses).
  function create(id = 'u1'): TestableDetail {
    paramMap$ = new BehaviorSubject(convertToParamMap({ id }));

    TestBed.configureTestingModule({
      imports: [AdminUserDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: convertToParamMap({ id }) },
            paramMap: paramMap$,
          },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminUserDetailComponent).componentInstance as TestableDetail;
  }

  function createLoaded(user: UserDetail = detail()): TestableDetail {
    const component = create(user.id);
    httpMock.expectOne(`${API}/users/${user.id}`).flush(user);
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

  // Hardening pass, 2026-09-25 (finding 6). A row fetched with a set missing
  // Member — the pre-existing seeded admin/approver shape — self-heals on
  // load rather than crashing the checkbox group or showing a state the UI
  // cannot represent (Member is always rendered checked).
  it('adds Member even if the loaded row did not have it', () => {
    const component = createLoaded(detail({ roles: ['Approver'] }));

    expect(component.selectedRoles()).toEqual(new Set(['Approver', 'Member']));
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

  // ---- Route reuse (hardening pass, 2026-09-25, finding 5) ----

  it('re-fetches when the route id changes without the component being recreated', () => {
    const component = createLoaded(detail({ id: 'u1', fullName: 'Member One' }));

    paramMap$.next(convertToParamMap({ id: 'u2' }));

    httpMock.expectOne(`${API}/users/u2`).flush(detail({ id: 'u2', fullName: 'Someone Else' }));

    expect(component.user()?.fullName).toBe('Someone Else');
  });

  // The exact scenario the finding describes: A starts loading, the route
  // changes to B before A resolves, B resolves first, and A's (later, stale)
  // response must never overwrite it. switchMap makes this true by
  // construction — it cancels A's in-flight request the moment B's id comes
  // through — rather than by a manual "is this still the current id" check.
  it('never lets a stale response for the previous person overwrite the current one', () => {
    const component = create('u1');
    const firstReq = httpMock.expectOne(`${API}/users/u1`);

    paramMap$.next(convertToParamMap({ id: 'u2' }));
    const secondReq = httpMock.expectOne(`${API}/users/u2`);

    // B resolves first...
    secondReq.flush(detail({ id: 'u2', fullName: 'Person B' }));
    expect(component.user()?.fullName).toBe('Person B');

    // ...and A's request was already cancelled by the switchMap, not merely
    // ignored — there is nothing left to "arrive late" at all.
    expect(firstReq.cancelled).toBe(true);
    expect(component.user()?.fullName).toBe('Person B');
    expect(component.loading()).toBe(false);
  });

  // A route change mid-edit must not leave the previous person's in-flight
  // write state showing against the new one.
  it('clears in-flight write state from the previous person when the route id changes', () => {
    const component = createLoaded(detail({ id: 'u1', roles: ['Member'] }));
    component.toggleRole('Approver');
    component.startConfirmingDeactivate();

    paramMap$.next(convertToParamMap({ id: 'u2' }));
    httpMock.expectOne(`${API}/users/u2`).flush(detail({ id: 'u2', roles: ['Member'] }));

    expect(component.confirmingDeactivate()).toBe(false);
    expect(component.selectedRoles()).toEqual(new Set(['Member']));
  });

  // ---- Roles ----

  it('tracks a toggle as a change against what was loaded', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));

    component.toggleRole('Approver');

    expect(component.selectedRoles()).toEqual(new Set(['Member', 'Approver']));
    expect(component.rolesDirty()).toBe(true);
    expect(component.canSaveRoles()).toBe(true);
  });

  // Hardening pass, 2026-09-25 (finding 6). Member can no longer be
  // unticked — every tenant user carries it regardless of any other role, and
  // the backend validator now refuses a set that omits it.
  it('cannot untick Member', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));

    component.toggleRole('Member');

    expect(component.selectedRoles()).toEqual(new Set(['Member']));
    expect(component.rolesDirty()).toBe(false);
  });

  it('saves the whole set and reseeds from the response', () => {
    const component = createLoaded(detail({ roles: ['Member'] }));
    component.toggleRole('Approver');

    component.saveRoles();
    const req = httpMock.expectOne(`${API}/users/u1/roles`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ roles: expect.arrayContaining(['Member', 'Approver']) });

    req.flush(writeResult({ roles: ['Approver', 'Member'] }));

    expect(component.rolesSaved()).toBe(true);
    expect(component.rolesDirty()).toBe(false);
    expect(component.savingRoles()).toBe(false);
  });

  // Decision `0031`, rendered as something to act on rather than a wall.
  it('renders LastTenantAdmin against the roles save', () => {
    const component = createLoaded(detail({ roles: ['TenantAdmin', 'Member'] }));
    component.toggleRole('TenantAdmin');

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
    req.flush(writeResult({ isActive: false }));

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
    const component = createLoaded(detail({ isActive: true, roles: ['TenantAdmin', 'Member'] }));

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
    req.flush(writeResult({ isActive: true }));

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

  // ---- Resend invitation (hardening pass, 2026-09-25, finding 3) ----

  it('offers resend for an active, not-yet-activated account', () => {
    const component = createLoaded(detail({ isActive: true, isActivated: false }));

    expect(component.canResendInvitation()).toBe(true);
  });

  it('does not offer resend once the account has activated', () => {
    const component = createLoaded(detail({ isActive: true, isActivated: true }));

    expect(component.canResendInvitation()).toBe(false);
  });

  it('does not offer resend to a deactivated account', () => {
    const component = createLoaded(detail({ isActive: false, isActivated: false }));

    expect(component.canResendInvitation()).toBe(false);
  });

  it('resends the invitation and reports success', () => {
    const component = createLoaded(detail({ isActive: true, isActivated: false }));

    component.resendInvitation();
    const req = httpMock.expectOne(`${API}/users/u1/invitation`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();

    req.flush({
      id: 'u1',
      email: 'member1@acme.test',
      fullName: 'Member One',
      activationLinkExpiresAtUtc: '2026-10-02T09:00:00Z',
      invitationEmailSent: true,
    });

    expect(component.resending()).toBe(false);
    expect(component.resendResult()?.invitationEmailSent).toBe(true);
  });

  it('reports a resend whose email did not go out, without treating it as a refusal', () => {
    const component = createLoaded(detail({ isActive: true, isActivated: false }));

    component.resendInvitation();
    httpMock.expectOne(`${API}/users/u1/invitation`).flush({
      id: 'u1',
      email: 'member1@acme.test',
      fullName: 'Member One',
      activationLinkExpiresAtUtc: '2026-10-02T09:00:00Z',
      invitationEmailSent: false,
    });

    expect(component.resendResult()?.invitationEmailSent).toBe(false);
    expect(component.resendRejection()).toBeNull();
  });

  it('renders UserAlreadyActivated against the resend action', () => {
    const component = createLoaded(detail({ isActive: true, isActivated: false }));

    component.resendInvitation();
    httpMock.expectOne(`${API}/users/u1/invitation`).flush(problem(409, 'UserAlreadyActivated'), {
      status: 409,
      statusText: 'Conflict',
    });

    expect(component.resendRejection()?.formMessage).toContain('already been activated');
    expect(component.resending()).toBe(false);
  });

  it('does not call the server when resend is offered to nobody', () => {
    const component = createLoaded(detail({ isActive: true, isActivated: true }));

    component.resendInvitation();

    httpMock.verify();
    expect(component.resending()).toBe(false);
  });
});
