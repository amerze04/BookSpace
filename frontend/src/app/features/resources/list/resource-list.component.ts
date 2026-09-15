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

// The "Approval" dropdown, client-side only (docs/wp7-plan.md, Phase 1: no
// `requiresApproval` query param exists on GET /resources).
export type ApprovalFilter = 'all' | 'required' | 'notRequired';

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

  // includeArchived is the one "More filters" checkbox this step adds — the
  // only real server parameter left that GET /resources supports and the
  // design didn't already surface via the type pills. It's a genuine
  // round-trip, exactly like selectedType.
  protected readonly includeArchived = signal(false);
  protected readonly showMoreFilters = signal(false);

  // searchText and approvalFilter are client-side only — filtered in
  // filteredItems() below over whatever `items` the last server fetch
  // returned, per the settled call in docs/wp7-plan.md (no `search` or
  // `requiresApproval` query param exists on GET /resources).
  protected readonly searchText = signal('');
  protected readonly approvalFilter = signal<ApprovalFilter>('all');

  protected readonly items = signal<ResourceSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // True only when the server reports more rows exist than this fetch
  // could carry (RESOURCE_LIST_PAGE_SIZE) — the honest alternative to
  // silently under-counting past the client-side-filtering limit the plan
  // accepted. Deliberately measured against `items()`, the raw fetch, not
  // `filteredItems()` — a search/approval filter narrowing what's *shown*
  // says nothing about whether more rows exist on the server than were ever
  // fetched.
  protected readonly isTruncated = computed(() => this.totalCount() > this.items().length);

  // The list actually rendered: `items()` (this fetch's up-to-100 rows, by
  // type and archived-state — both real server filters) narrowed by the
  // client-side search text and approval-required state. Search matches
  // only the resource's name — a list row carries no description to search
  // against (step 2's own deviation note) — case-insensitively, by simple
  // substring rather than any fuzzier match the API doesn't support either.
  protected readonly filteredItems = computed(() => {
    const query = this.searchText().trim().toLowerCase();
    const approval = this.approvalFilter();

    return this.items().filter((resource) => {
      if (query && !resource.name.toLowerCase().includes(query)) {
        return false;
      }
      if (approval === 'required' && !resource.requiresApproval) {
        return false;
      }
      if (approval === 'notRequired' && resource.requiresApproval) {
        return false;
      }
      return true;
    });
  });

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

  protected onSearchInput(event: Event): void {
    this.searchText.set((event.target as HTMLInputElement).value);
  }

  protected onApprovalFilterChange(event: Event): void {
    this.approvalFilter.set((event.target as HTMLSelectElement).value as ApprovalFilter);
  }

  protected toggleMoreFilters(): void {
    this.showMoreFilters.set(!this.showMoreFilters());
  }

  // A real server round-trip, unlike search/approval — see includeArchived's
  // own comment above.
  protected onIncludeArchivedChange(event: Event): void {
    this.includeArchived.set((event.target as HTMLInputElement).checked);
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
      .list({
        pageSize: RESOURCE_LIST_PAGE_SIZE,
        type: this.selectedType() ?? undefined,
        // Only sent when true, matching `type`'s own "omit at the default"
        // convention (ResourcesService.buildListParams) — the backend's own
        // default is already false, so there's nothing to say otherwise.
        includeArchived: this.includeArchived() ? true : undefined,
      })
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
