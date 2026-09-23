import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../../resources/services/resources.service';
import {
  RESOURCE_TYPES,
  ResourceDetail,
  ResourceType,
  TimeZoneChangeNotice,
  UpdateResourceRequest,
} from '../../../resources/models/resources.models';
import { resourceTypeLabel } from '../../../../shared/resource-type/resource-type';
import { ResourceRejection, describeResourceRejection } from '../../rejection/resource-rejection';
import { defaultTimeZoneId, supportedTimeZoneIds, timeZoneLabel } from '../../timezones/timezone-catalog';

// Admin console phase 3. Create, edit and archive — FR-3.1 and FR-3.5.
//
// **One component for create and edit**, chosen rather than two. Every field is
// shared, the three refusals of `docs/admin-plan.md` §4.5 are shared, and the
// only differences are the heading, the CTA, whether an id is loaded first, and
// the two sections that exist in edit mode alone. Two components would be two
// copies of the field vocabulary, and the first to drift would be the one
// nobody was looking at — the same argument that made `DecisionPanelComponent`
// one component with two hosts in WP-7.
//
// Archive lives here and **not on the list row**, deliberately. It cannot be
// undone, so the one thing worth buying is that the administrator is looking at
// the resource — its name, its type, its capacity — at the moment they decide.
// A row action in a list of fifty is how the wrong thing gets archived.
//
// Three conventions inherited from WP-7 and not re-litigated: everything
// feeding a submit is disabled while it is in flight, the outcome renders from
// what came back rather than from form state, and a server field message
// outranks a client one until its own control is edited.

// Mirrors ResourceFieldRules' column-width caps so an oversized value is a
// message under the control rather than a 400 round trip. The server is still
// the authority — these are the message, not the guarantee.
const NAME_MAX_LENGTH = 200;
const DESCRIPTION_MAX_LENGTH = 1000;

type FormMode = 'create' | 'edit';

@Component({
  selector: 'app-admin-resource-form',
  imports: [RouterLink],
  templateUrl: './admin-resource-form.component.html',
  styleUrl: './admin-resource-form.component.scss',
})
export class AdminResourceFormComponent {
  private readonly resourcesService = inject(ResourcesService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly resourceTypes = RESOURCE_TYPES;
  protected readonly typeLabel = resourceTypeLabel;
  protected readonly timeZoneLabel = timeZoneLabel;
  protected readonly nameMaxLength = NAME_MAX_LENGTH;
  protected readonly descriptionMaxLength = DESCRIPTION_MAX_LENGTH;

  // Read once: a form is not a screen a route id changes underneath, unlike the
  // resource detail page, which is reached by clicking between resources.
  private readonly resourceId = this.route.snapshot.paramMap.get('id');
  protected readonly mode: FormMode = this.resourceId === null ? 'create' : 'edit';

  protected readonly timeZoneIds = supportedTimeZoneIds();

  // ---- Field state ----

  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly resourceType = signal<ResourceType>('Room');
  protected readonly capacity = signal('1');
  protected readonly timeZoneId = signal(defaultTimeZoneId(this.timeZoneIds));
  protected readonly requiresApproval = signal(false);
  protected readonly minDurationMinutes = signal('');
  protected readonly maxDurationMinutes = signal('');

  // ---- Screen state ----

  protected readonly loading = signal(this.mode === 'edit');
  protected readonly loadError = signal(false);
  protected readonly notFound = signal(false);
  protected readonly submitting = signal(false);
  protected readonly rejection = signal<ResourceRejection | null>(null);

  // The loaded resource, in edit mode. Kept whole rather than only its fields,
  // because two decisions below need what a field cannot answer: whether it is
  // archived, and whether it has approvers.
  protected readonly loaded = signal<ResourceDetail | null>(null);

  // Rendered from the response, never from form state.
  protected readonly createdId = signal<string | null>(null);
  protected readonly saved = signal(false);
  protected readonly timeZoneChange = signal<TimeZoneChangeNotice | null>(null);

  // ---- Archive ----

  protected readonly confirmingArchive = signal(false);
  protected readonly archiveAcknowledged = signal(false);
  protected readonly archiving = signal(false);
  protected readonly archived = signal(false);

  // An archived resource refuses every edit (FR-3.5, `ResourceArchived`), so
  // the form goes read-only rather than offering a save that cannot succeed.
  // True both for a resource that arrived archived and one archived here.
  protected readonly isArchived = computed(() => this.archived() || (this.loaded()?.isArchived ?? false));

  protected readonly heading = computed(() =>
    this.mode === 'create' ? 'New resource' : (this.loaded()?.name ?? 'Resource'),
  );

  // **`docs/admin-plan.md` §4.3, answered by decision 0028 rather than by this
  // form.** The flag used to be unavailable until the resource had approvers,
  // which this screen rendered as a disabled control with an explanation.
  //
  // That rule is gone, and the reason is worth carrying here because it is the
  // whole point of the control: the flag and the approver list are set by
  // different endpoints, so requiring approvers first forced every gated
  // resource through a window in which it existed and was **freely bookable** —
  // the exact state the rule existed to prevent. A resource is now gated from
  // the moment it is created, and a TenantAdmin can decide on its requests
  // until approvers are assigned.
  //
  // So the control is always available. What remains is a warning, not a
  // refusal: an administrator turning this on with nobody assigned should know
  // who is going to be notified meanwhile.
  protected readonly approverCount = computed(() => this.loaded()?.approvers.length ?? 0);

  // True while the resource is gated with nobody assigned — the legal but
  // worth-saying state 0028 introduced. On the create form it is whatever the
  // admin has just ticked, since a new resource has no approvers by definition.
  protected readonly gatedWithoutApprovers = computed(
    () => this.requiresApproval() && this.approverCount() === 0,
  );

  // ---- Client-side field messages ----
  //
  // A server message outranks a client one until its control is edited, which
  // is why each of these checks the rejection first.

  protected readonly nameError = computed(() => {
    const serverMessage = this.rejection()?.fieldMessages.name;
    if (serverMessage) {
      return serverMessage;
    }
    const value = this.name().trim();
    if (value.length === 0) {
      return 'A name is required.';
    }
    if (value.length > NAME_MAX_LENGTH) {
      return `A name can be at most ${NAME_MAX_LENGTH} characters.`;
    }
    return null;
  });

  protected readonly descriptionError = computed(() => {
    const serverMessage = this.rejection()?.fieldMessages.description;
    if (serverMessage) {
      return serverMessage;
    }
    return this.description().length > DESCRIPTION_MAX_LENGTH
      ? `A description can be at most ${DESCRIPTION_MAX_LENGTH} characters.`
      : null;
  });

  // Decision `0005`: capacity counts **concurrent units**, not seats, so zero
  // would mean a resource nothing can ever be booked on. That is why the floor
  // is 1 rather than 0, and why the message says units.
  protected readonly capacityError = computed(() => {
    const serverMessage = this.rejection()?.fieldMessages.capacity;
    if (serverMessage) {
      return serverMessage;
    }
    const value = parseIntegerField(this.capacity());
    if (value === 'invalid' || value === null) {
      return 'Capacity is required.';
    }
    return value < 1 ? 'Capacity is the number of units that can be booked at once, so it must be at least 1.' : null;
  });

  protected readonly minDurationError = computed(() =>
    this.rejection()?.fieldMessages.minDurationMinutes ?? durationFieldError(this.minDurationMinutes()),
  );

  protected readonly maxDurationError = computed(() => {
    const serverMessage = this.rejection()?.fieldMessages.maxDurationMinutes;
    if (serverMessage) {
      return serverMessage;
    }

    const ownError = durationFieldError(this.maxDurationMinutes());
    if (ownError) {
      return ownError;
    }

    const min = parseIntegerField(this.minDurationMinutes());
    const max = parseIntegerField(this.maxDurationMinutes());
    if (typeof min === 'number' && typeof max === 'number' && max < min) {
      return 'The longest booking cannot be shorter than the shortest.';
    }
    return null;
  });

  protected readonly timeZoneError = computed(() => this.rejection()?.fieldMessages.timeZoneId ?? null);

  protected readonly approvalError = computed(() => this.rejection()?.fieldMessages.requiresApproval ?? null);

  protected readonly hasFieldErrors = computed(
    () =>
      this.nameError() !== null ||
      this.descriptionError() !== null ||
      this.capacityError() !== null ||
      this.minDurationError() !== null ||
      this.maxDurationError() !== null,
  );

  protected readonly canSubmit = computed(
    () => !this.submitting() && !this.isArchived() && !this.hasFieldErrors(),
  );

  constructor() {
    if (this.mode === 'edit' && this.resourceId !== null) {
      this.load(this.resourceId);
    }

    // The override has to be cleared, or this resource's name lingers onto
    // whatever page the breadcrumb walk computes next.
    this.destroyRef.onDestroy(() => this.breadcrumbService.setOverride(null));
  }

  // ---- Input handlers ----
  //
  // Each clears its own server message, so a message about a box whose contents
  // have since changed does not sit under it.

  protected onNameInput(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
    this.clearServerFieldMessage('name');
  }

  protected onDescriptionInput(event: Event): void {
    this.description.set((event.target as HTMLTextAreaElement).value);
    this.clearServerFieldMessage('description');
  }

  protected onTypeChange(event: Event): void {
    this.resourceType.set((event.target as HTMLSelectElement).value as ResourceType);
    this.clearServerFieldMessage('resourceType');
  }

  protected onCapacityInput(event: Event): void {
    this.capacity.set((event.target as HTMLInputElement).value);
    this.clearServerFieldMessage('capacity');
  }

  protected onTimeZoneChange(event: Event): void {
    this.timeZoneId.set((event.target as HTMLSelectElement).value);
    this.clearServerFieldMessage('timeZoneId');
  }

  protected onRequiresApprovalChange(event: Event): void {
    this.requiresApproval.set((event.target as HTMLInputElement).checked);
    this.clearServerFieldMessage('requiresApproval');
  }

  protected onMinDurationInput(event: Event): void {
    this.minDurationMinutes.set((event.target as HTMLInputElement).value);
    this.clearServerFieldMessage('minDurationMinutes');
  }

  protected onMaxDurationInput(event: Event): void {
    this.maxDurationMinutes.set((event.target as HTMLInputElement).value);
    this.clearServerFieldMessage('maxDurationMinutes');
  }

  protected retryLoad(): void {
    if (this.resourceId !== null) {
      this.load(this.resourceId);
    }
  }

  // ---- Submit ----

  protected submit(): void {
    if (!this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.rejection.set(null);
    this.saved.set(false);
    this.timeZoneChange.set(null);

    const request = this.buildRequest();

    if (this.mode === 'create') {
      this.resourcesService.create(request).subscribe({
        next: (response) => {
          this.submitting.set(false);
          this.createdId.set(response.id);
        },
        error: (error: unknown) => this.handleWriteError(error),
      });
      return;
    }

    this.resourcesService.update(this.resourceId!, request).subscribe({
      next: (response) => {
        this.submitting.set(false);
        this.saved.set(true);
        this.timeZoneChange.set(response.timeZoneChange);

        // Re-seed from the response rather than leaving the typed values in
        // place: the server is what decides what was actually stored, and a
        // trimmed name or a cleared duration should show as such.
        const current = this.loaded();
        if (current !== null) {
          this.loaded.set({ ...current, ...response });
          this.breadcrumbService.setOverride(response.name);
        }
        this.applyFields(response);
      },
      error: (error: unknown) => this.handleWriteError(error),
    });
  }

  // ---- Archive ----

  protected startConfirmingArchive(): void {
    this.confirmingArchive.set(true);
    this.archiveAcknowledged.set(false);
    this.rejection.set(null);
  }

  protected cancelConfirmingArchive(): void {
    this.confirmingArchive.set(false);
    this.archiveAcknowledged.set(false);
  }

  protected onArchiveAcknowledgedChange(event: Event): void {
    this.archiveAcknowledged.set((event.target as HTMLInputElement).checked);
  }

  protected confirmArchive(): void {
    if (!this.archiveAcknowledged() || this.archiving() || this.resourceId === null) {
      return;
    }

    this.archiving.set(true);
    this.rejection.set(null);

    this.resourcesService.archive(this.resourceId).subscribe({
      next: () => {
        this.archiving.set(false);
        this.confirmingArchive.set(false);
        this.archived.set(true);
      },
      error: (error: unknown) => {
        this.archiving.set(false);
        this.rejection.set(describeResourceRejection(error));
      },
    });
  }

  // ---- Internals ----

  private buildRequest(): UpdateResourceRequest {
    return {
      name: this.name().trim(),
      // An empty box means "no description", not an empty string — the column
      // is nullable and the read renders null as absent.
      description: this.description().trim() || null,
      resourceType: this.resourceType(),
      capacity: Number(this.capacity()),
      timeZoneId: this.timeZoneId(),
      // Never sent as true from the create screen: the control is disabled
      // there, and the signal cannot have been flipped.
      requiresApproval: this.requiresApproval(),
      minDurationMinutes: toNullableMinutes(this.minDurationMinutes()),
      maxDurationMinutes: toNullableMinutes(this.maxDurationMinutes()),
    };
  }

  private handleWriteError(error: unknown): void {
    this.submitting.set(false);
    const rejection = describeResourceRejection(error);
    this.rejection.set(rejection);

    // A 404 mid-edit means the resource left this caller's reach — archived
    // out from under them is not the case (archiving keeps it readable), so
    // this is the AC-4 answer or a genuinely deleted row. The form has nothing
    // left to edit either way.
    if (rejection.resourceNotFound) {
      this.notFound.set(true);
    }
  }

  private clearServerFieldMessage(field: keyof ResourceRejection['fieldMessages']): void {
    const rejection = this.rejection();
    if (rejection?.fieldMessages[field]) {
      this.rejection.set(null);
    }
  }

  private load(id: string): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);

    this.resourcesService.getById(id).subscribe({
      next: (resource) => {
        this.loaded.set(resource);
        this.applyFields(resource);
        this.breadcrumbService.setOverride(resource.name);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        if (describeResourceRejection(error).resourceNotFound) {
          this.notFound.set(true);
        } else {
          this.loadError.set(true);
        }
      },
    });
  }

  // Seeds the controls from a server representation — the loaded detail, or an
  // update response. A null duration is an empty box, not a zero.
  private applyFields(resource: {
    name: string;
    description: string | null;
    resourceType: ResourceType;
    capacity: number;
    timeZoneId: string;
    requiresApproval: boolean;
    minDurationMinutes: number | null;
    maxDurationMinutes: number | null;
  }): void {
    this.name.set(resource.name);
    this.description.set(resource.description ?? '');
    this.resourceType.set(resource.resourceType);
    this.capacity.set(String(resource.capacity));
    this.requiresApproval.set(resource.requiresApproval);
    this.minDurationMinutes.set(resource.minDurationMinutes === null ? '' : String(resource.minDurationMinutes));
    this.maxDurationMinutes.set(resource.maxDurationMinutes === null ? '' : String(resource.maxDurationMinutes));

    // **Only if the picker actually offers it.** The two ICU catalogues — the
    // browser's and the server's — can disagree at the edges, and a stored id
    // this host does not list would otherwise leave the select showing its
    // first option while the admin believes they are looking at the resource's
    // real timezone. Saving then silently moves it. Keeping the stored value
    // out of a select that cannot represent it is caught by the template, which
    // shows the stored id as a plain read-only value in that case.
    if (this.timeZoneIds.includes(resource.timeZoneId)) {
      this.timeZoneId.set(resource.timeZoneId);
    }
  }

  protected readonly timeZoneUnavailable = computed(() => {
    const stored = this.loaded()?.timeZoneId;
    return stored !== undefined && !this.timeZoneIds.includes(stored) ? stored : null;
  });

  // The template needs the id for the opening-hours link, and `resourceId` is
  // private because nothing else should reach for it. Null on the create form,
  // where the link is not rendered at all.
  protected resourceIdOrNull(): string | null {
    return this.resourceId;
  }

  protected goToList(): void {
    void this.router.navigate(['/admin/resources']);
  }
}

// '' -> null (the field is optional), a whole number -> that number, anything
// else -> 'invalid'. Separate from the null case so a box holding "abc" is not
// silently treated as "no limit".
function parseIntegerField(raw: string): number | null | 'invalid' {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }
  if (!/^-?\d+$/.test(trimmed)) {
    return 'invalid';
  }
  return Number(trimmed);
}

function toNullableMinutes(raw: string): number | null {
  const value = parseIntegerField(raw);
  return typeof value === 'number' ? value : null;
}

// CK_Resources_DurationLimits, restated as a message. Null on either bound
// means "no limit", so the rule only fires when a value was supplied.
function durationFieldError(raw: string): string | null {
  const value = parseIntegerField(raw);
  if (value === 'invalid') {
    return 'Enter a whole number of minutes, or leave this empty for no limit.';
  }
  if (typeof value === 'number' && value < 1) {
    return 'A duration limit must be at least 1 minute. Leave it empty for no limit.';
  }
  return null;
}
