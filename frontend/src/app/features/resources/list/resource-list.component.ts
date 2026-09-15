import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ResourcesService } from '../resources.service';
import { ResourceSummary, ResourceType } from '../resources.models';

// The API's own page-size ceiling (PagingDefaults.MaxPageSize). Fetching one
// page at this size and filtering client-side is the settled answer
// (docs/wp7-plan.md, Phase 1) for the search/approval filters step 3 adds —
// step 2 already fetches at this size so switching to a type pill never
// needs a second round-trip shape later.
const RESOURCE_LIST_PAGE_SIZE = 100;

interface TypeFilterOption {
  label: string;
  value: ResourceType | null;
}

// `null` stands for "All" — deliberately not sent as a query param at all
// (ResourcesService.list treats an unset `type` as "every type"), rather than
// this file inventing a sentinel value the backend has never heard of.
const TYPE_FILTERS: TypeFilterOption[] = [
  { label: 'All', value: null },
  { label: 'Rooms', value: 'Room' },
  { label: 'Equipment', value: 'Equipment' },
  { label: 'Vehicles', value: 'Vehicle' },
  { label: 'Lab slots', value: 'LabSlot' },
  { label: 'Other', value: 'Other' },
];

// The singular label shown on a card, as opposed to TYPE_FILTERS' plural
// pill labels ("Rooms" the filter, "Room" the resource) — two different
// pieces of copy for the same enum value, not one reused awkwardly for both.
const RESOURCE_TYPE_LABELS: Record<ResourceType, string> = {
  Room: 'Room',
  Equipment: 'Equipment',
  Vehicle: 'Vehicle',
  LabSlot: 'Lab slot',
  Other: 'Other',
};

@Component({
  selector: 'app-resource-list',
  imports: [RouterLink],
  templateUrl: './resource-list.component.html',
  styleUrl: './resource-list.component.scss',
})
export class ResourceListComponent {
  private readonly resourcesService = inject(ResourcesService);
  private readonly router = inject(Router);

  protected readonly typeFilters = TYPE_FILTERS;
  protected readonly selectedType = signal<ResourceType | null>(null);

  protected readonly items = signal<ResourceSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // True only when the server reports more rows exist than this fetch
  // could carry (RESOURCE_LIST_PAGE_SIZE) — the honest alternative to
  // silently under-counting past the client-side-filtering limit the plan
  // accepted.
  protected readonly isTruncated = computed(() => this.totalCount() > this.items().length);

  // Guards against a stale response overwriting a newer one if a second
  // pill is clicked before the first request returns — the same
  // "which one is still current" idea as AuthService.sessionGeneration,
  // applied to requests instead of sessions.
  private latestRequestId = 0;

  constructor() {
    this.load();
  }

  protected selectType(type: ResourceType | null): void {
    if (type === this.selectedType()) {
      return;
    }
    this.selectedType.set(type);
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected goToDetail(resourceId: string): void {
    void this.router.navigate(['/resources', resourceId]);
  }

  protected typeLabel(type: ResourceType): string {
    return RESOURCE_TYPE_LABELS[type];
  }

  // Derived purely from Capacity, never from ResourceType or the resource's
  // name — decisions/0005 already makes Capacity the one axis that means
  // exclusive vs. pooled, and CLAUDE.md §11 rules out inventing a rule like
  // "Hot Desk Area says 'Multiple desks'" that isn't backed by any field the
  // API actually returns.
  protected capacityLabel(resource: ResourceSummary): string {
    return resource.capacity === 1 ? 'Single resource' : `${resource.capacity} units`;
  }

  private load(): void {
    const requestId = ++this.latestRequestId;
    this.loading.set(true);
    this.loadError.set(false);

    this.resourcesService
      .list({ pageSize: RESOURCE_LIST_PAGE_SIZE, type: this.selectedType() ?? undefined })
      .subscribe({
        next: (page) => {
          if (requestId !== this.latestRequestId) {
            return; // a newer request already landed; this one is stale
          }
          this.items.set(page.items);
          this.totalCount.set(page.totalCount);
          this.loading.set(false);
        },
        error: () => {
          if (requestId !== this.latestRequestId) {
            return;
          }
          this.loading.set(false);
          this.loadError.set(true);
        },
      });
  }
}
