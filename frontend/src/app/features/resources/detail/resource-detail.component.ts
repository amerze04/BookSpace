import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BreadcrumbService } from '../../../layout/breadcrumb.service';
import { ResourcesService } from '../resources.service';
import { AvailabilityWindowDetail, DayOfWeekName, ResourceDetail, ResourceType } from '../resources.models';

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

// Duplicated from ResourceListComponent rather than extracted — the same
// "third occurrence" rule brand-mark was extracted under (WP-6 Phase 3):
// this is only the second place either mapping is needed. Extract a shared
// module once a third feature (the booking form, most likely) needs the
// same type icon or label.
const RESOURCE_TYPE_LABELS: Record<ResourceType, string> = {
  Room: 'Room',
  Equipment: 'Equipment',
  Vehicle: 'Vehicle',
  LabSlot: 'Lab slot',
  Other: 'Other',
};

@Component({
  selector: 'app-resource-detail',
  imports: [RouterLink],
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

  constructor() {
    const initialId = this.route.snapshot.paramMap.get('id');
    if (!initialId) {
      throw new Error('ResourceDetailComponent requires an "id" route param.');
    }
    this.resourceId = initialId;

    // Subscribed rather than read once from the snapshot: the router reuses
    // this component if it's ever navigated from one resource's detail
    // straight to another's (the same route config, just a different :id),
    // and a snapshot taken at construction would go stale — the same
    // "don't trust a one-time read" lesson ShellComponent's own breadcrumb
    // fix already applied, for a different reason.
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const id = params.get('id');
      if (id && id !== this.resourceId) {
        this.resourceId = id;
        this.load();
      }
    });

    this.load();

    // Never leave another page showing this resource's name in its
    // breadcrumb after navigating away.
    this.destroyRef.onDestroy(() => this.breadcrumbService.setOverride(null));
  }

  protected retry(): void {
    this.load();
  }

  protected typeLabel(type: ResourceType): string {
    return RESOURCE_TYPE_LABELS[type];
  }

  protected capacityLabel(resource: ResourceDetail): string {
    return resource.capacity === 1 ? 'Single resource' : `${resource.capacity} units`;
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

  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);
    this.breadcrumbService.setOverride(null);

    this.resourcesService.getById(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.loading.set(false);
        this.breadcrumbService.setOverride(resource.name);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        if (error instanceof HttpErrorResponse && error.status === 404) {
          this.notFound.set(true);
        } else {
          this.loadError.set(true);
        }
      },
    });
  }
}
