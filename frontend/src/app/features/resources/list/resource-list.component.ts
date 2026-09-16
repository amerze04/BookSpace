import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { ResourcesService } from '../resources.service';
import { ResourceSummary, ResourceType } from '../resources.models';
import { ResourceTypeIconComponent } from '../../../shared/resource-type/resource-type-icon.component';
import { resourceCapacityLabel, resourceTypeLabel } from '../../../shared/resource-type/resource-type';

// A generous page size — a tenant with over 100 resources is still the
// exception CLAUDE.md's own filter-gap note flagged, so this stays large
// enough that Previous/Next rarely has anything to do — but no longer the
// *only* way to reach the rest (item 8): GET /resources already returns
// real page metadata (PagedResult's totalPages/hasNextPage/hasPreviousPage),
// so paging through it properly needed no backend change, just using what
// was already on the wire.
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

// The "Approval" dropdown's three states, mapped onto
// ListResourcesParams.requiresApproval (true / false / omitted) in load() —
// a real server round-trip since 2026-09-15, not a client-side predicate.
export type ApprovalFilter = 'all' | 'required' | 'notRequired';

@Component({
  selector: 'app-resource-list',
  imports: [RouterLink, ResourceTypeIconComponent],
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

  // Real pagination (item 8), not a "narrow your filters" truncation
  // notice: page/totalPages/hasPreviousPage/hasNextPage all come straight
  // off the server's own PagedResult, never recomputed client-side (the
  // same "one source, not two that could disagree" reasoning
  // core/http/paged-result.ts's own comment already applies to
  // totalPages/hasNextPage/hasPreviousPage being mirrored rather than
  // derived).
  protected readonly page = signal(1);
  protected readonly totalPages = signal(1);
  protected readonly hasPreviousPage = signal(false);
  protected readonly hasNextPage = signal(false);

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
      .subscribe(() => this.resetToFirstPageAndLoad());
  }

  protected selectType(type: ResourceType | null): void {
    if (type === this.selectedType()) {
      return;
    }
    this.selectedType.set(type);
    this.resetToFirstPageAndLoad();
  }

  protected onSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchText.set(value);
    this.searchRequestChanges$.next(value);
  }

  protected onApprovalFilterChange(event: Event): void {
    this.approvalFilter.set((event.target as HTMLSelectElement).value as ApprovalFilter);
    this.resetToFirstPageAndLoad();
  }

  protected toggleMoreFilters(): void {
    this.showMoreFilters.set(!this.showMoreFilters());
  }

  protected onIncludeArchivedChange(event: Event): void {
    this.includeArchived.set((event.target as HTMLInputElement).checked);
    this.resetToFirstPageAndLoad();
  }

  protected retry(): void {
    this.load();
  }

  // Item 8: any filter changing the *set* of matching resources has to go
  // back to page 1 — staying on, say, page 3 of a filter that now has only
  // one page would otherwise show "no results" for a filter that actually
  // has plenty, just not that far in.
  private resetToFirstPageAndLoad(): void {
    this.page.set(1);
    this.load();
  }

  protected goToPreviousPage(): void {
    if (!this.hasPreviousPage()) {
      return;
    }
    this.page.update((p) => p - 1);
    this.load();
  }

  protected goToNextPage(): void {
    if (!this.hasNextPage()) {
      return;
    }
    this.page.update((p) => p + 1);
    this.load();
  }

  protected goToDetail(resourceId: string): void {
    void this.router.navigate(['/resources', resourceId]);
  }

  // Delegates to the shared helper (extracted 2026-09-16, WP-7 Phase 2) —
  // kept as a method here rather than called directly from the template so
  // existing call sites and tests don't change shape.
  protected typeLabel(type: ResourceType): string {
    return resourceTypeLabel(type);
  }

  protected capacityLabel(resource: ResourceSummary): string {
    return resourceCapacityLabel(resource);
  }

  private load(): void {
    const requestId = ++this.latestRequestId;
    this.loading.set(true);
    this.loadError.set(false);

    const approval = this.approvalFilter();

    this.resourcesService
      .list({
        page: this.page(),
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
        next: (result) => {
          if (requestId !== this.latestRequestId) {
            return; // a newer request already landed; this one is stale
          }
          this.items.set(result.items);
          this.totalCount.set(result.totalCount);
          this.totalPages.set(result.totalPages);
          this.hasPreviousPage.set(result.hasPreviousPage);
          this.hasNextPage.set(result.hasNextPage);
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
