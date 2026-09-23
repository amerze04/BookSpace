import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../../resources/services/resources.service';
import { DayOfWeekName, ResourceDetail } from '../../../resources/models/resources.models';
import { AvailabilityRejection, describeAvailabilityRejection } from '../../rejection/availability-rejection';
import {
  EDITOR_WEEKDAYS,
  WindowRow,
  hasChanges,
  newRowKey,
  toRows,
  toWindowPayload,
  validateRows,
} from '../../windows/window-editor';

// Admin console phase 4. The weekly availability editor — FR-3.2.
//
// **An "edit everything, save once" form, not a list with inline add and
// delete**, and that is the API's shape rather than a preference:
// `PUT /resources/{id}/availability-windows` replaces the whole set, so an
// omitted window is a deleted one. Making each row save itself would mean one
// request per keystroke-group against an endpoint that rewrites the schedule
// every time — and a half-applied weekly schedule if one of them failed.
//
// Blackout periods (phase 6) are genuine per-row CRUD and will look different
// on purpose. `docs/admin-plan.md` §4.1 records why the two must not be made to
// look alike.
//
// The rules live in `windows/window-editor.ts` as pure functions, the way
// `recurrence-form.ts` holds the booking form's: the interesting part is
// arithmetic over times, and arithmetic is worth testing without a TestBed.
@Component({
  selector: 'app-admin-availability-windows',
  imports: [RouterLink],
  templateUrl: './admin-availability-windows.component.html',
  styleUrl: './admin-availability-windows.component.scss',
})
export class AdminAvailabilityWindowsComponent {
  private readonly resourcesService = inject(ResourcesService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly weekdays = EDITOR_WEEKDAYS;

  private readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  protected readonly resource = signal<ResourceDetail | null>(null);
  protected readonly rows = signal<WindowRow[]>([]);

  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);
  protected readonly notFound = signal(false);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly rejection = signal<AvailabilityRejection | null>(null);

  // What the server last confirmed, so "has anything changed" is answered
  // against the truth rather than against whatever the form started as.
  private readonly savedWindows = signal<ResourceDetail['availabilityWindows']>([]);

  protected readonly errors = computed(() => validateRows(this.rows()));

  protected readonly dirty = computed(() => hasChanges(this.rows(), this.savedWindows()));

  // FR-3.5: an archived resource accepts no edits, and this endpoint refuses
  // them with `ResourceArchived` like every other write. Read-only rather than
  // a save that cannot succeed.
  protected readonly isArchived = computed(() => this.resource()?.isArchived ?? false);

  protected readonly canSave = computed(
    () => !this.saving() && !this.isArchived() && this.dirty() && this.errors().length === 0,
  );

  // The resource's own timezone, which is what these times are written in —
  // decision `0003`, and the single most misreadable thing on this screen. A
  // window is a recurring weekly rule with no offset to carry, so 09:00 means
  // 09:00 where the resource is, never where the administrator is.
  protected readonly timeZoneId = computed(() => this.resource()?.timeZoneId ?? '');

  constructor() {
    this.load();
    this.destroyRef.onDestroy(() => {
      this.breadcrumbService.setOverride(null);
      this.breadcrumbService.setInsertBeforeLast(null);
    });
  }

  protected rowsFor(weekday: DayOfWeekName): WindowRow[] {
    return this.rows().filter((row) => row.weekday === weekday);
  }

  protected errorFor(key: string): string | null {
    return this.errors().find((e) => e.key === key)?.message ?? null;
  }

  protected addRow(weekday: DayOfWeekName): void {
    // 09:00–17:00 rather than empty boxes: almost every schedule is an office
    // day, and a row that arrives pre-filled with the common answer is one an
    // admin adjusts instead of composes. It is still a full row, so the
    // validation rules apply to it unchanged.
    this.rows.update((rows) => [
      ...rows,
      { key: newRowKey(), weekday, opensAt: '09:00', closesAt: '17:00', closesAtEndOfDay: false },
    ]);
    this.clearOutcome();
  }

  protected removeRow(key: string): void {
    this.rows.update((rows) => rows.filter((row) => row.key !== key));
    this.clearOutcome();
  }

  protected onOpensAtInput(key: string, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.updateRow(key, (row) => ({ ...row, opensAt: value }));
  }

  protected onClosesAtInput(key: string, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.updateRow(key, (row) => ({ ...row, closesAt: value }));
  }

  // Decision `0022`: ticking this stores 23:59:59, which the whole system reads
  // as the following midnight. The flag is kept separate from the time so the
  // editor never has to guess whether a stored 23:59:59 meant the convention or
  // meant one second to midnight — indistinguishable on the wire, and they must
  // not be on screen.
  protected onEndOfDayChange(key: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.updateRow(key, (row) => ({ ...row, closesAtEndOfDay: checked }));
  }

  protected retryLoad(): void {
    this.load();
  }

  protected save(): void {
    if (!this.canSave()) {
      return;
    }

    this.saving.set(true);
    this.rejection.set(null);
    this.saved.set(false);

    const payload = toWindowPayload(this.rows());

    this.resourcesService.replaceAvailabilityWindows(this.resourceId, payload).subscribe({
      next: (response) => {
        this.saving.set(false);
        this.saved.set(true);

        // Re-seed from the response, never from what was typed: the server
        // assigned ids and normalized the order, and this is what makes `dirty`
        // fall back to false without a second read.
        this.savedWindows.set(response.availabilityWindows);
        this.rows.set(toRows(response.availabilityWindows));

        const current = this.resource();
        if (current !== null) {
          this.resource.set({ ...current, availabilityWindows: response.availabilityWindows });
        }
      },
      error: (error: unknown) => {
        this.saving.set(false);
        const rejection = describeAvailabilityRejection(error);
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

  private updateRow(key: string, update: (row: WindowRow) => WindowRow): void {
    this.rows.update((rows) => rows.map((row) => (row.key === key ? update(row) : row)));
    this.clearOutcome();
  }

  // A "saved" banner must not outlive the state it described, and a server
  // rejection must not sit above a form that has since changed.
  private clearOutcome(): void {
    this.saved.set(false);
    this.rejection.set(null);
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);

    this.resourcesService.getById(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.savedWindows.set(resource.availabilityWindows);
        this.rows.set(toRows(resource.availabilityWindows));
        this.loading.set(false);

        // The resource's name is inserted *before* the last crumb rather than
        // replacing it: the route chain already ends in "Availability", which
        // has to stay. Same shape the member-facing availability screen uses.
        this.breadcrumbService.setInsertBeforeLast(resource.name);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        if (describeAvailabilityRejection(error).resourceNotFound) {
          this.notFound.set(true);
        } else {
          this.loadError.set(true);
        }
      },
    });
  }
}
