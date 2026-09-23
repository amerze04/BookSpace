import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { AdminResourceFormComponent } from '../components/admin-resource-form/admin-resource-form.component';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { ResourceDetail, TimeZoneChangeNotice } from '../../resources/models/resources.models';

const API = 'http://localhost:5270';

type TestableForm = AdminResourceFormComponent & {
  mode: 'create' | 'edit';
  name: () => string;
  description: () => string;
  capacity: () => string;
  timeZoneId: () => string;
  requiresApproval: () => boolean;
  minDurationMinutes: () => string;
  maxDurationMinutes: () => string;
  loading: () => boolean;
  loadError: () => boolean;
  notFound: () => boolean;
  submitting: () => boolean;
  saved: () => boolean;
  archived: () => boolean;
  isArchived: () => boolean;
  createdId: () => string | null;
  canRequireApproval: () => boolean;
  canSubmit: () => boolean;
  nameError: () => string | null;
  capacityError: () => string | null;
  minDurationError: () => string | null;
  maxDurationError: () => string | null;
  timeZoneUnavailable: () => string | null;
  timeZoneChange: () => TimeZoneChangeNotice | null;
  rejection: () => { formMessage: string | null; fieldMessages: Record<string, string> } | null;
  confirmingArchive: () => boolean;
  archiveAcknowledged: () => boolean;
  onNameInput(event: Event): void;
  onDescriptionInput(event: Event): void;
  onCapacityInput(event: Event): void;
  onTimeZoneChange(event: Event): void;
  onRequiresApprovalChange(event: Event): void;
  onMinDurationInput(event: Event): void;
  onMaxDurationInput(event: Event): void;
  submit(): void;
  startConfirmingArchive(): void;
  cancelConfirmingArchive(): void;
  onArchiveAcknowledgedChange(event: Event): void;
  confirmArchive(): void;
};

function input(value: string): Event {
  return { target: { value } } as unknown as Event;
}

function checkbox(checked: boolean): Event {
  return { target: { checked } } as unknown as Event;
}

function problem(status: number, reasonCode: string, errors?: Record<string, string[]>) {
  return {
    status,
    title: 'The request was refused.',
    reasonCode,
    ...(errors ? { errors } : {}),
  };
}

function detail(overrides: Partial<ResourceDetail> = {}): ResourceDetail {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: 'Main conference room',
    resourceType: 'Room',
    capacity: 1,
    timeZoneId: 'Europe/Warsaw',
    requiresApproval: false,
    minDurationMinutes: 30,
    maxDurationMinutes: 240,
    isArchived: false,
    createdAtUtc: '2026-09-01T08:00:00Z',
    updatedAtUtc: '2026-09-01T08:00:00Z',
    availabilityWindows: [],
    approvers: [],
    ...overrides,
  };
}

function updateResponse(overrides: Record<string, unknown> = {}) {
  const base = detail();
  return {
    id: base.id,
    name: base.name,
    description: base.description,
    resourceType: base.resourceType,
    capacity: base.capacity,
    timeZoneId: base.timeZoneId,
    requiresApproval: base.requiresApproval,
    minDurationMinutes: base.minDurationMinutes,
    maxDurationMinutes: base.maxDurationMinutes,
    isArchived: false,
    createdAtUtc: base.createdAtUtc,
    updatedAtUtc: '2026-09-23T09:00:00Z',
    timeZoneChange: null,
    ...overrides,
  };
}

describe('AdminResourceFormComponent', () => {
  let httpMock: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  // `id` null means the create route (/admin/resources/new), which carries no
  // `:id` at all — that absence is what the component reads its mode from.
  function create(id: string | null): TestableForm {
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [AdminResourceFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate, createUrlTree: vi.fn(), serializeUrl: () => '' } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: (key: string) => (key === 'id' ? id : null) } } },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(AdminResourceFormComponent).componentInstance as TestableForm;
  }

  function createLoaded(resource: ResourceDetail = detail()): TestableForm {
    const component = create(resource.id);
    httpMock.expectOne(`${API}/resources/${resource.id}`).flush(resource);
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

  // ---- Mode ----

  it('is a create form when the route carries no id, and fetches nothing', () => {
    const component = create(null);

    expect(component.mode).toBe('create');
    expect(component.loading()).toBe(false);
    httpMock.expectNone((r) => r.url.startsWith(`${API}/resources`));
  });

  it('is an edit form when the route carries an id, and seeds every control from the resource', () => {
    const component = createLoaded();

    expect(component.mode).toBe('edit');
    expect(component.name()).toBe('Conference Room A');
    expect(component.description()).toBe('Main conference room');
    expect(component.capacity()).toBe('1');
    expect(component.timeZoneId()).toBe('Europe/Warsaw');
    expect(component.minDurationMinutes()).toBe('30');
    expect(component.maxDurationMinutes()).toBe('240');
  });

  // A null duration is an empty box, never a zero — a zero would be a limit,
  // and "no limit" is what null means.
  it('renders an unset duration limit as an empty box, not as 0', () => {
    const component = createLoaded(detail({ minDurationMinutes: null, maxDurationMinutes: null }));

    expect(component.minDurationMinutes()).toBe('');
    expect(component.maxDurationMinutes()).toBe('');
  });

  it('shows a not-found state for a resource that is not this tenant’s to manage', () => {
    const component = create('gone');
    httpMock
      .expectOne(`${API}/resources/gone`)
      .flush(problem(404, 'ResourceNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
    expect(component.loadError()).toBe(false);
  });

  it('shows a retryable error for a load failure that is not a 404', () => {
    const component = create('r1');
    httpMock.expectOne(`${API}/resources/r1`).flush(null, { status: 500, statusText: 'Server Error' });

    expect(component.loadError()).toBe(true);
    expect(component.notFound()).toBe(false);
  });

  it('puts the resource name in the breadcrumb once it has loaded', () => {
    createLoaded();

    expect(TestBed.inject(BreadcrumbService).override()).toBe('Conference Room A');
  });

  // ---- FR-3.3, the §4.3 answer ----

  // **The structural case.** A resource that does not exist cannot have
  // approvers, and approvers are assigned by a different endpoint — so
  // `requiresApproval` can only ever be false at creation. The control is
  // rendered and disabled rather than hidden, so the setting is discoverable.
  it('cannot require approval on create, because a new resource has no approvers', () => {
    const component = create(null);

    expect(component.canRequireApproval()).toBe(false);
  });

  it('still cannot require approval on edit while the resource has no approvers', () => {
    const component = createLoaded(detail({ approvers: [] }));

    expect(component.canRequireApproval()).toBe(false);
  });

  it('opens the approval control as soon as the resource has an approver', () => {
    const component = createLoaded(detail({ approvers: [{ userId: 'u9', fullName: 'Resource Approver' }] }));

    expect(component.canRequireApproval()).toBe(true);
  });

  it('renders the approval control disabled with the reason, rather than hiding it', () => {
    const component = create(null);
    const fixture = TestBed.createComponent(AdminResourceFormComponent);
    fixture.detectChanges();

    const dom = fixture.nativeElement as HTMLElement;
    const approvalBox = dom.querySelector<HTMLInputElement>('.field--checkbox input[type="checkbox"]');

    expect(approvalBox).not.toBeNull();
    expect(approvalBox!.disabled).toBe(true);
    expect(dom.textContent).toContain('has no approvers yet');
    expect(component.mode).toBe('create');
  });

  // ---- Client-side field rules ----

  it('refuses an empty name before spending a request on it', () => {
    const component = create(null);
    component.onNameInput(input('   '));

    expect(component.nameError()).toContain('required');
    expect(component.canSubmit()).toBe(false);

    component.submit();
    httpMock.expectNone(`${API}/resources`);
  });

  // Decision `0005`: capacity counts concurrent units, so zero would mean a
  // resource nothing can ever be booked on.
  it('refuses a capacity below one, and says what capacity means', () => {
    const component = create(null);
    component.onNameInput(input('Printer'));
    component.onCapacityInput(input('0'));

    expect(component.capacityError()).toContain('units');
    expect(component.canSubmit()).toBe(false);
  });

  it('refuses a longest booking shorter than the shortest', () => {
    const component = create(null);
    component.onNameInput(input('Room'));
    component.onMinDurationInput(input('60'));
    component.onMaxDurationInput(input('30'));

    expect(component.maxDurationError()).toContain('cannot be shorter');
  });

  // An empty duration box means "no limit", so it must not be treated as an
  // error — but a box holding "abc" must not be silently treated as empty.
  it('treats an empty duration as no limit and a non-numeric one as a mistake', () => {
    const component = create(null);
    component.onNameInput(input('Room'));

    component.onMinDurationInput(input(''));
    expect(component.minDurationError()).toBeNull();

    component.onMinDurationInput(input('abc'));
    expect(component.minDurationError()).toContain('whole number');
  });

  // ---- Create ----

  it('sends exactly what the create endpoint expects, with an empty description as null', () => {
    const component = create(null);
    component.onNameInput(input('  3D Printer  '));
    component.onDescriptionInput(input('   '));
    component.onCapacityInput(input('3'));
    component.onMinDurationInput(input('15'));

    component.submit();

    const request = httpMock.expectOne(`${API}/resources`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toMatchObject({
      name: '3D Printer',
      description: null,
      capacity: 3,
      requiresApproval: false,
      minDurationMinutes: 15,
      maxDurationMinutes: null,
    });

    request.flush({ ...updateResponse(), id: 'new-1' });
    expect(component.createdId()).toBe('new-1');
  });

  // Rendered from the 201, not from form state, and the screen stays put: the
  // next step is the admin's to pick.
  it('offers the new resource and the list once creation succeeds', () => {
    const component = create(null);
    component.onNameInput(input('Van'));
    component.submit();
    httpMock.expectOne(`${API}/resources`).flush({ ...updateResponse(), id: 'v1' });

    const fixture = TestBed.createComponent(AdminResourceFormComponent);
    fixture.detectChanges();

    expect(component.createdId()).toBe('v1');
    expect(navigate).not.toHaveBeenCalled();
  });

  // ---- Edit ----

  // **PUT is a full representation, not a patch** (decision `0015`): every
  // mutable field goes, and an omitted nullable one means cleared. A form that
  // sent only what changed would silently wipe the rest.
  it('sends a full representation on save, including fields the admin did not touch', () => {
    const component = createLoaded();
    component.onNameInput(input('Conference Room B'));

    component.submit();

    const request = httpMock.expectOne(`${API}/resources/r1`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      name: 'Conference Room B',
      description: 'Main conference room',
      resourceType: 'Room',
      capacity: 1,
      timeZoneId: 'Europe/Warsaw',
      requiresApproval: false,
      minDurationMinutes: 30,
      maxDurationMinutes: 240,
    });

    request.flush(updateResponse({ name: 'Conference Room B' }));
    expect(component.saved()).toBe(true);
  });

  it('re-seeds the controls and the breadcrumb from the response rather than from what was typed', () => {
    const component = createLoaded();
    component.onNameInput(input('  Trimmed By Server  '));
    component.submit();

    httpMock.expectOne(`${API}/resources/r1`).flush(updateResponse({ name: 'Trimmed By Server' }));

    expect(component.name()).toBe('Trimmed By Server');
    expect(TestBed.inject(BreadcrumbService).override()).toBe('Trimmed By Server');
  });

  // The one thing an edit does that its own fields do not show. A window is
  // stored as resource-local wall-clock time (decision `0003`), so changing the
  // timezone **reinterprets** windows rather than shifting them.
  it('surfaces the timezone-change notice, with the window count it reports', () => {
    const component = createLoaded();
    component.onTimeZoneChange(input('UTC'));
    component.submit();

    httpMock.expectOne(`${API}/resources/r1`).flush(
      updateResponse({
        timeZoneId: 'UTC',
        timeZoneChange: {
          previousTimeZoneId: 'Europe/Warsaw',
          newTimeZoneId: 'UTC',
          reinterpretedAvailabilityWindowCount: 5,
        },
      }),
    );

    expect(component.timeZoneChange()?.reinterpretedAvailabilityWindowCount).toBe(5);
  });

  it('shows no timezone notice when the timezone did not change', () => {
    const component = createLoaded();
    component.onNameInput(input('Renamed'));
    component.submit();
    httpMock.expectOne(`${API}/resources/r1`).flush(updateResponse({ name: 'Renamed' }));

    expect(component.timeZoneChange()).toBeNull();
  });

  // The stored id is not one this browser's ICU data lists, so the select
  // cannot represent it. Letting the select fall to its first option while the
  // admin believes they are looking at the real timezone is how saving moves it
  // silently.
  it('warns rather than silently substituting when the stored timezone is unknown here', () => {
    const component = createLoaded(detail({ timeZoneId: 'Mars/Olympus_Mons' }));

    expect(component.timeZoneUnavailable()).toBe('Mars/Olympus_Mons');
    expect(component.timeZoneId()).not.toBe('Mars/Olympus_Mons');
  });

  // ---- Refusals ----

  it('puts a capacity refusal on the capacity control, with the pending-bookings explanation', () => {
    const component = createLoaded();
    component.onCapacityInput(input('1'));
    component.submit();

    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(422, 'CapacityBelowExistingBookings'), { status: 422, statusText: 'Unprocessable' });

    expect(component.capacityError()).toContain('Pending requests count too');
  });

  it('puts a concurrency conflict above the form and tells the admin to reload', () => {
    const component = createLoaded();
    component.onNameInput(input('Renamed'));
    component.submit();

    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(409, 'ConcurrencyConflict'), { status: 409, statusText: 'Conflict' });

    expect(component.rejection()?.formMessage).toContain('Reload');
    expect(component.submitting()).toBe(false);
  });

  // A server message must not sit under a box whose contents have since
  // changed.
  it('clears a server field message once its own control is edited', () => {
    const component = createLoaded();
    component.submit();
    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(400, 'ValidationFailed', { Name: ['Name must not be empty.'] }), {
        status: 400,
        statusText: 'Bad Request',
      });
    expect(component.nameError()).toContain('must not be empty');

    component.onNameInput(input('A proper name'));

    expect(component.nameError()).toBeNull();
  });

  it('switches to the not-found state when the resource disappears mid-edit', () => {
    const component = createLoaded();
    component.onNameInput(input('Renamed'));
    component.submit();

    httpMock
      .expectOne(`${API}/resources/r1`)
      .flush(problem(404, 'ResourceNotFound'), { status: 404, statusText: 'Not Found' });

    expect(component.notFound()).toBe(true);
  });

  // ---- Archive ----

  it('requires the acknowledgement before it will archive anything', () => {
    const component = createLoaded();
    component.startConfirmingArchive();

    expect(component.confirmingArchive()).toBe(true);
    expect(component.archiveAcknowledged()).toBe(false);

    component.confirmArchive();
    httpMock.expectNone(`${API}/resources/r1/archive`);
  });

  it('archives once acknowledged, and posts to the archive transition rather than DELETE', () => {
    const component = createLoaded();
    component.startConfirmingArchive();
    component.onArchiveAcknowledgedChange(checkbox(true));

    component.confirmArchive();

    const request = httpMock.expectOne(`${API}/resources/r1/archive`);
    expect(request.request.method).toBe('POST');
    request.flush({ ...updateResponse(), isArchived: true });

    expect(component.archived()).toBe(true);
    expect(component.isArchived()).toBe(true);
  });

  // FR-3.5: an archived resource stays readable but accepts no edits, so the
  // form must not offer a save that cannot succeed.
  it('goes read-only once archived, and refuses to submit', () => {
    const component = createLoaded();
    component.startConfirmingArchive();
    component.onArchiveAcknowledgedChange(checkbox(true));
    component.confirmArchive();
    httpMock.expectOne(`${API}/resources/r1/archive`).flush({ ...updateResponse(), isArchived: true });

    expect(component.canSubmit()).toBe(false);

    component.submit();
    httpMock.expectNone(`${API}/resources/r1`);
  });

  it('opens read-only for a resource that was already archived', () => {
    const component = createLoaded(detail({ isArchived: true }));

    expect(component.isArchived()).toBe(true);
    expect(component.canSubmit()).toBe(false);
  });

  it('forgets the acknowledgement if the confirmation is dismissed and reopened', () => {
    const component = createLoaded();
    component.startConfirmingArchive();
    component.onArchiveAcknowledgedChange(checkbox(true));
    component.cancelConfirmingArchive();
    component.startConfirmingArchive();

    expect(component.archiveAcknowledged()).toBe(false);
  });

  // ---- Rendered output ----

  // The confirmation has to say both things: that there is no way back, and
  // what happens to bookings that already exist. Leaving the second out makes
  // archiving look more destructive than it is — nothing is cancelled.
  it('says archiving is final and that existing bookings survive it', () => {
    const component = createLoaded();
    component.startConfirmingArchive();

    const fixture = TestBed.createComponent(AdminResourceFormComponent);
    httpMock.expectOne(`${API}/resources/r1`).flush(detail());
    fixture.detectChanges();

    // The second fixture has its own instance, so drive that one's panel open.
    const instance = fixture.componentInstance as TestableForm;
    instance.startConfirmingArchive();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('cannot be undone');
    expect(text).toContain('not');
    expect(text).toContain('cancelled');
  });

  it('shows no archive section at all on the create form', () => {
    create(null);
    const fixture = TestBed.createComponent(AdminResourceFormComponent);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.danger-zone')).toBeNull();
  });
});
