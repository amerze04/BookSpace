import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { ResourcesService } from '../resources.service';
import { ResourceSummary, ResourceType } from '../resources.models';

// A generous page rather than a real pagination UI — there is no client-side
// filtering left to justify fetching the API's own max (that workaround is
// gone, see the 2026-09-15 note below), but nothing yet asks for a "next
// page" control either, and a tenant with over 100 resources is still the
// exception CLAUDE.md's own filter-gap note flagged as worth revisiting only
// if it actually happens.
const RESOURCE_LIST_PAGE_SIZE = 100;

// How long the search box waits after the last keystroke before firing a
// request — every keystroke is now a real HTTP round-trip (search moved
// server-side 2026-09-15), so debouncing is what keeps a fast typist from
// firing a dozen requests for one search term.
const SEARCH_DEBOUNCE_MS = 300;

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

// The "Approval" dropdown's three states, mapped onto
// ListResourcesParams.requiresApproval (true / false / omitted) in load() —
// a real server round-trip since 2026-09-15, not a client-side predicate.
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

  // includeArchived, search and approvalFilter are all real server
  // round-trips (docs/wp7-plan.md, Phase 1 — search/approval moved server-side
  // 2026-09-15, once GET /resources actually supported them; see CLAUDE.md's
  // "Resource list filters extended for WP-7" entry). None of the four
  // filters on this screen do any client-side narrowing any more — `items`
  // below is exactly what the last fetch returned.
  protected readonly includeArchived = signal(false);
  protected readonly showMoreFilters = signal(false);
  protected readonly approvalFilter = signal<ApprovalFilter>('all');

  // The immediate value the search box displays — updated on every
  // keystroke so the input never feels laggy — separate from
  // searchRequestChanges$ below, which is what actually triggers a request,
  // debounced.
  protected readonly searchText = signal('');
  private readonly searchRequestChanges$ = new Subject<string>();

  protected readonly items = signal<ResourceSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // True only when the server reports more rows exist than this fetch could
  // carry (RESOURCE_LIST_PAGE_SIZE) — meaningful again now that every filter
  // on this screen narrows the fetch itself rather than merely what's shown.
  protected readonly isTruncated = computed(() => this.totalCount() > this.items().length);

  // Guards against a stale response overwriting a newer one if a second
  // request goes out before the first returns (a type pill click racing a
  // debounced search, for instance) — the same "which one is still current"
  // idea as AuthService.sessionGeneration, applied to requests instead of
  // sessions.
  private latestRequestId = 0;

  constructor() {
    this.load();

    // distinctUntilChanged so clearing the box back to what it already was
    // (or a debounce window elapsing with no real change) doesn't spend a
    // request saying nothing new; takeUntilDestroyed so this subscription
    // doesn't outlive the component.
    this.searchRequestChanges$
      .pipe(debounceTime(SEARCH_DEBOUNCE_MS), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe(() => this.load());
  }

  protected selectType(type: ResourceType | null): void {
    if (type === this.selectedType()) {
      return;
    }
    this.selectedType.set(type);
    this.load();
  }

  protected onSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchText.set(value);
    this.searchRequestChanges$.next(value);
  }

  protected onApprovalFilterChange(event: Event): void {
    this.approvalFilter.set((event.target as HTMLSelectElement).value as ApprovalFilter);
    this.load();
  }

  protected toggleMoreFilters(): void {
    this.showMoreFilters.set(!this.showMoreFilters());
  }

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

    const approval = this.approvalFilter();

    this.resourcesService
      .list({
        pageSize: RESOURCE_LIST_PAGE_SIZE,
        type: this.selectedType() ?? undefined,
        // Only sent when it says something other than the backend's own
        // default (ResourcesService.buildListParams's "omit at the
        // default" convention): includeArchived only when true, search
        // only when non-empty, requiresApproval only when the dropdown
        // has actually narrowed it.
        includeArchived: this.includeArchived() ? true : undefined,
        search: this.searchText().trim() || undefined,
        requiresApproval: approval === 'all' ? undefined : approval === 'required',
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
