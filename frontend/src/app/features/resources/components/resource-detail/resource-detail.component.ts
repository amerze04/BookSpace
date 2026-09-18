import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, distinctUntilChanged, filter, map, merge, of, switchMap } from 'rxjs';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../services/resources.service';
import { AvailabilityWindowDetail, DayOfWeekName, ResourceDetail, ResourceType } from '../../models/resources.models';
import { ResourceTypeIconComponent } from '../../../../shared/resource-type/resource-type-icon.component';
import { resourceCapacityLabel, resourceTypeLabel } from '../../../../shared/resource-type/resource-type';
import { recurringEntryQueryParams } from '../../../booking/arrival/booking-arrival';

// Monday-first, matching the design — not the DayOfWeek enum's own
// Sunday-first declaration order, which nothing on the wire promises anyway
// (weekday serializes as its name, not its ordinal).
const WEEKDAY_ORDER: DayOfWeekName[] = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
];

interface WeekdayRow {
  weekday: DayOfWeekName;
  hours: string | null;
}

// One row per calendar weekday, always all seven regardless of how many
// windows the resource actually has — a day with no window is "Not bookable"
// on its own row, not an absent one. Multiple windows on the same weekday
// (a split shift) join with a comma; the repository already orders them by
// opening time, so this doesn't need to re-sort.
function buildWeekdayRows(windows: readonly AvailabilityWindowDetail[]): WeekdayRow[] {
  return WEEKDAY_ORDER.map((weekday) => {
    const windowsForDay = windows.filter((w) => w.weekday === weekday);
    if (windowsForDay.length === 0) {
      return { weekday, hours: null };
    }
    return {
      weekday,
      hours: windowsForDay.map((w) => `${formatTime(w.opensAt)} – ${formatTime(w.closesAt)}`).join(', '),
    };
  });
}

// "08:00:00" -> "08:00". OpensAt/ClosesAt are TimeOnly's default JSON format
// (HH:mm:ss); seconds are never meaningful here (CK_AvailabilityWindows_Window
// works in whole seconds, but nothing in this UI needs to show them).
function formatTime(hhmmss: string): string {
  return hhmmss.slice(0, 5);
}

type ResourceLoadResult = { kind: 'success'; resource: ResourceDetail } | { kind: 'error'; error: unknown };

function formatDuration(minutes: number | null, whenUnset: string): string {
  if (minutes === null) {
    return whenUnset;
  }
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  const hourLabel = `${hours} ${hours === 1 ? 'hour' : 'hours'}`;
  return remainder === 0 ? hourLabel : `${hourLabel} ${remainder} min`;
}

@Component({
  selector: 'app-resource-detail',
  imports: [RouterLink, ResourceTypeIconComponent],
  templateUrl: './resource-detail.component.html',
  styleUrl: './resource-detail.component.scss',
})
export class ResourceDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly resourcesService = inject(ResourcesService);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly resource = signal<ResourceDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // Kept distinct from loadError: a 404 here is AC-4's usual shape (a
  // nonexistent id and another tenant's real one are byte-identical), so the
  // message deliberately says neither "it doesn't exist" nor "it isn't
  // yours" — and unlike a transient failure, retrying the same id can't
  // help, so this state offers a way back to the list instead of a retry
  // button.
  protected readonly notFound = signal(false);

  protected readonly weekdayRows = computed(() => buildWeekdayRows(this.resource()?.availabilityWindows ?? []));

  private resourceId: string;

  // Fed by retry() alongside route-id changes, so both funnel through the
  // same switchMap below rather than each racing it with a separate call to
  // a shared load() method.
  private readonly retry$ = new Subject<void>();

  constructor() {
    const initialId = this.route.snapshot.paramMap.get('id');
    if (!initialId) {
      throw new Error('ResourceDetailComponent requires an "id" route param.');
    }
    this.resourceId = initialId;

    // Observed rather than read once from the snapshot: the router reuses
    // this component if it's ever navigated from one resource's detail
    // straight to another's (the same route config, just a different :id),
    // and a snapshot taken at construction would go stale — the same
    // "don't trust a one-time read" lesson ShellComponent's own breadcrumb
    // fix already applied, for a different reason.
    //
    // switchMap (item 4) is what makes a stale response impossible rather
    // than merely ignored: a route-id change or a retry() both cancel
    // whatever fetch is still in flight (switchMap unsubscribes the
    // previous inner Observable, which aborts the underlying HTTP request)
    // before starting the next one, so an older response can never arrive
    // after a newer one and overwrite it.
    const idChanges$ = this.route.paramMap.pipe(
      map((params) => params.get('id')),
      filter((id): id is string => !!id),
      distinctUntilChanged(),
    );

    merge(idChanges$, this.retry$.pipe(map(() => this.resourceId)))
      .pipe(
        map((id) => {
          this.resourceId = id;
          return id;
        }),
        switchMap((id) => this.fetchResource$(id)),
        takeUntilDestroyed(),
      )
      .subscribe((result) => this.applyResult(result));

    // Never leave another page showing this resource's name in its
    // breadcrumb after navigating away.
    this.destroyRef.onDestroy(() => this.breadcrumbService.setOverride(null));
  }

  // The recurring entry point's own query params, from the booking feature's
  // contract module rather than spelled out here — the same reason the
  // availability screen imports its writer instead of repeating the parameter
  // names (see booking-arrival.ts).
  protected readonly recurringEntryQueryParams = recurringEntryQueryParams();

  protected retry(): void {
    this.retry$.next();
  }

  // Delegates to the shared helper (extracted 2026-09-16, WP-7 Phase 2, once
  // the availability screen became a third caller) — kept as a method here
  // rather than called directly from the template so existing call sites and
  // tests don't change shape.
  protected typeLabel(type: ResourceType): string {
    return resourceTypeLabel(type);
  }

  protected capacityLabel(resource: ResourceDetail): string {
    return resourceCapacityLabel(resource);
  }

  protected minDurationLabel(resource: ResourceDetail): string {
    return formatDuration(resource.minDurationMinutes, 'No minimum');
  }

  protected maxDurationLabel(resource: ResourceDetail): string {
    return formatDuration(resource.maxDurationMinutes, 'No maximum');
  }

  protected approverNames(resource: ResourceDetail): string {
    return resource.approvers.length > 0 ? resource.approvers.map((a) => a.fullName).join(', ') : 'None assigned';
  }

  private fetchResource$(id: string): Observable<ResourceLoadResult> {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);
    this.breadcrumbService.setOverride(null);

    return this.resourcesService.getById(id).pipe(
      map((resource) => ({ kind: 'success' as const, resource })),
      catchError((error: unknown) => of({ kind: 'error' as const, error })),
    );
  }

  private applyResult(result: ResourceLoadResult): void {
    this.loading.set(false);
    if (result.kind === 'error') {
      if (result.error instanceof HttpErrorResponse && result.error.status === 404) {
        this.notFound.set(true);
      } else {
        this.loadError.set(true);
      }
      return;
    }

    this.resource.set(result.resource);
    this.breadcrumbService.setOverride(result.resource.name);
  }
}
