import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, forkJoin } from 'rxjs';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../../resources/services/resources.service';
import { UsersService } from '../../services/users.service';
import { ApproverDetail, ResourceDetail } from '../../../resources/models/resources.models';
import { EligibleUser } from '../../models/users.models';
import { ApproverRejection, describeApproverRejection } from '../../rejection/approver-rejection';
import {
  ApproverOption,
  hasChanges,
  roleLabel,
  strandedApprovers,
  toApproverPayload,
  toOptions,
} from '../../approvers/approver-selection';

// Admin console phase 5. Who may decide on this resource's bookings — FR-3.3.
//
// **The picker only ever offers people who are already eligible**, and that is
// not politeness — it is forced by decision `0018`. Every reason an assignment
// can be refused (wrong tenant, wrong role, deactivated) collapses into one
// `ApproverNotEligible` code, deliberately, because naming the cause would
// confirm that a cross-tenant id exists somewhere. So a picker that let an
// admin type a user id could only ever answer "no" without saying why. Offering
// the eligible set is the only shape that can explain itself.
//
// Two reads, one screen, and they do not agree with each other — see
// `approvers/approver-selection.ts` for the gap between them and the silent
// removal it would otherwise cause.
//
// Replace-the-set, like the availability editor: one save for the whole list,
// because `PUT /resources/{id}/approvers` rewrites it and an omitted id is a
// removal.

const PICKER_PAGE_SIZE = 50;
const SEARCH_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-admin-approvers',
  imports: [RouterLink],
  templateUrl: './admin-approvers.component.html',
  styleUrl: './admin-approvers.component.scss',
})
export class AdminApproversComponent {
  private readonly resourcesService = inject(ResourcesService);
  private readonly usersService = inject(UsersService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly roleLabel = roleLabel;

  private readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  protected readonly resource = signal<ResourceDetail | null>(null);
  protected readonly eligible = signal<EligibleUser[]>([]);

  // The selection, held as ids rather than as flags on the options: the option
  // list is replaced wholesale on every search, and a selection stored on those
  // objects would be lost each time somebody typed.
  protected readonly selectedUserIds = signal<ReadonlySet<string>>(new Set());

  // What the server last confirmed, which is what "has anything changed" is
  // answered against.
  private readonly assigned = signal<ApproverDetail[]>([]);

  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);
  protected readonly notFound = signal(false);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly rejection = signal<ApproverRejection | null>(null);

  protected readonly searchText = signal('');
  private readonly searchChanges$ = new Subject<string>();
  protected readonly searchingUsers = signal(false);
  protected readonly totalEligible = signal(0);

  protected readonly options = computed<ApproverOption[]>(() =>
    toOptions(this.eligible(), this.selectedUserIds()),
  );

  // Assigned people `GET /users` will not offer — deactivated since they were
  // assigned, most likely. Empty in every ordinary tenant; rendered separately
  // when it is not, because the alternative is removing somebody silently.
  //
  // Computed against the **unsearched** eligible set would be ideal, but the
  // picker is paged and searched, so a person merely on another page would look
  // stranded. It is therefore only trusted when the picker is showing
  // everything: no search term, and one page.
  protected readonly stranded = computed(() =>
    this.pickerShowsEveryone() ? strandedApprovers(this.assigned(), this.eligible()) : [],
  );

  private readonly pickerShowsEveryone = computed(
    () => this.searchText().trim() === '' && this.eligible().length >= this.totalEligible(),
  );

  protected readonly isArchived = computed(() => this.resource()?.isArchived ?? false);

  protected readonly dirty = computed(() => hasChanges(this.selectedUserIds(), this.assigned()));

  protected readonly canSave = computed(
    () => !this.saving() && !this.isArchived() && this.dirty(),
  );

  protected readonly selectedCount = computed(() => this.selectedUserIds().size);

  // Decision `0028`: a gated resource with nobody assigned is legal, and its
  // requests fall to the tenant's admins. Worth saying on the one screen that
  // can produce that state on purpose.
  protected readonly gatedWithNobody = computed(
    () => (this.resource()?.requiresApproval ?? false) && this.selectedCount() === 0,
  );

  constructor() {
    this.load();

    this.searchChanges$
      .pipe(debounceTime(SEARCH_DEBOUNCE_MS), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe(() => this.loadUsers());

    this.destroyRef.onDestroy(() => {
      this.breadcrumbService.setOverride(null);
      this.breadcrumbService.setInsertBeforeLast(null);
    });
  }

  protected onSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchText.set(value);
    this.searchChanges$.next(value);
  }

  protected toggle(userId: string): void {
    this.selectedUserIds.update((current) => {
      const next = new Set(current);
      if (next.has(userId)) {
        next.delete(userId);
      } else {
        next.add(userId);
      }
      return next;
    });

    this.saved.set(false);
    this.rejection.set(null);
  }

  protected retryLoad(): void {
    this.load();
  }

  protected save(): void {
    if (!this.canSave()) {
      return;
    }

    this.saving.set(true);
    this.saved.set(false);
    this.rejection.set(null);

    this.resourcesService
      .replaceApprovers(this.resourceId, toApproverPayload(this.selectedUserIds()))
      .subscribe({
        next: (response) => {
          this.saving.set(false);
          this.saved.set(true);

          // Re-seeded from the response, never from the selection: the server
          // resolved every id to a person and normalized the order, and this is
          // what makes `dirty` fall back to false without a second read.
          this.assigned.set(response.approvers);
          this.selectedUserIds.set(new Set(response.approvers.map((a) => a.userId)));

          const current = this.resource();
          if (current !== null) {
            this.resource.set({
              ...current,
              approvers: response.approvers,
              requiresApproval: response.requiresApproval,
            });
          }
        },
        error: (error: unknown) => {
          this.saving.set(false);
          const rejection = describeApproverRejection(error);
          this.rejection.set(rejection);

          if (rejection.resourceNotFound) {
            this.notFound.set(true);
          }
        },
      });
  }

  protected goToResource(): void {
    void this.router.navigate(['/admin/resources', this.resourceId]);
  }

  // Both reads in one go. `forkJoin` rather than two independent subscriptions
  // because the two answers are only meaningful together: the ticks on the
  // picker come from the resource, and which people are pickable comes from
  // `/users`. Rendering either alone would show a list with the wrong ticks.
  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);

    forkJoin({
      resource: this.resourcesService.getById(this.resourceId),
      users: this.usersService.list({ pageSize: PICKER_PAGE_SIZE }),
    }).subscribe({
      next: ({ resource, users }) => {
        this.resource.set(resource);
        this.assigned.set(resource.approvers);
        this.selectedUserIds.set(new Set(resource.approvers.map((a) => a.userId)));
        this.eligible.set(users.items);
        this.totalEligible.set(users.totalCount);
        this.loading.set(false);

        this.breadcrumbService.setInsertBeforeLast(resource.name);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        if (describeApproverRejection(error).resourceNotFound) {
          this.notFound.set(true);
        } else {
          this.loadError.set(true);
        }
      },
    });
  }

  // The picker's own reload, on search. Deliberately does not touch the
  // selection: a person ticked and then searched away from is still assigned,
  // and losing them because they scrolled out of view would be the same silent
  // removal the stranded list exists to prevent.
  private loadUsers(): void {
    this.searchingUsers.set(true);

    this.usersService
      .list({ pageSize: PICKER_PAGE_SIZE, search: this.searchText().trim() || undefined })
      .subscribe({
        next: (users) => {
          this.eligible.set(users.items);
          this.totalEligible.set(users.totalCount);
          this.searchingUsers.set(false);
        },
        error: () => {
          this.searchingUsers.set(false);
          this.loadError.set(true);
        },
      });
  }
}
