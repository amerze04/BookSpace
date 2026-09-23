import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { ResourcesService } from '../../../resources/services/resources.service';
import { ResourceSummary } from '../../../resources/models/resources.models';
import { ResourceTypeIconComponent } from '../../../../shared/resource-type/resource-type-icon.component';
import { resourceCapacityLabel, resourceTypeLabel } from '../../../../shared/resource-type/resource-type';

// Admin console phase 3. The catalogue, as an administrator sees it.
//
// **A separate screen from `/resources`, not a mode on it** (settled in phase
// 2). The two answer different questions: the member list is "find something to
// book" and hides archived rows by design (FR-3.5), while this one is "manage
// the catalogue" and has to be able to show them. Every row here leads to an
// edit form; no row here offers a booking.
//
// **Deliberately fewer filters than the member list.** That screen has search,
// five type pills, an approval dropdown and an archived toggle, because
// browsing is its whole job. An admin arrives knowing which resource they came
// to change, so this screen keeps the two controls that serve that — find by
// name, and see the archived ones — and leaves the rest out rather than
// copying them because they exist.

const ADMIN_RESOURCE_PAGE_SIZE = 50;

// Same debounce as the member list: every keystroke is a real round trip, since
// search moved server-side on 2026-09-15.
const SEARCH_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-admin-resource-list',
  imports: [RouterLink, ResourceTypeIconComponent],
  templateUrl: './admin-resource-list.component.html',
  styleUrl: './admin-resource-list.component.scss',
})
export class AdminResourceListComponent {
  private readonly resourcesService = inject(ResourcesService);

  // **`includeArchived`, not an archived-only view**, and that is the API's
  // shape rather than a preference — checked against the real endpoint in this
  // phase, as `docs/admin-plan.md` §6 asked. `GET /resources` offers
  // `includeArchived` (a widening) and nothing that narrows *to* archived.
  //
  // Filtering a fetched page down to the archived rows client-side would be the
  // mistake phase 1 avoided in SQL: the page boundaries and `totalCount` would
  // still describe the unfiltered set, so "3 resources" would sit above a list
  // of one. The toggle says what the parameter actually does.
  protected readonly includeArchived = signal(false);

  protected readonly searchText = signal('');
  private readonly searchRequestChanges$ = new Subject<string>();

  protected readonly items = signal<ResourceSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  protected readonly page = signal(1);
  protected readonly totalPages = signal(1);
  protected readonly hasPreviousPage = signal(false);
  protected readonly hasNextPage = signal(false);

  // A newer request's result must not be overwritten by an older one landing
  // late — a toggle click racing a debounced search. Same idea as the member
  // list's own guard.
  private latestRequestId = 0;

  constructor() {
    this.load();

    this.searchRequestChanges$
      .pipe(debounceTime(SEARCH_DEBOUNCE_MS), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe(() => this.resetToFirstPageAndLoad());
  }

  protected onSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchText.set(value);
    this.searchRequestChanges$.next(value);
  }

  protected onIncludeArchivedChange(event: Event): void {
    this.includeArchived.set((event.target as HTMLInputElement).checked);
    this.resetToFirstPageAndLoad();
  }

  protected retry(): void {
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

  protected typeLabel = resourceTypeLabel;

  protected capacityLabel(resource: ResourceSummary): string {
    return resourceCapacityLabel(resource);
  }

  // Any change to the *set* of matching resources goes back to page 1 — staying
  // on page 3 of a filter that now has one page shows "no resources" for a
  // filter that has plenty.
  private resetToFirstPageAndLoad(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    const requestId = ++this.latestRequestId;
    this.loading.set(true);
    this.loadError.set(false);

    this.resourcesService
      .list({
        page: this.page(),
        pageSize: ADMIN_RESOURCE_PAGE_SIZE,
        // Omitted at the backend's own default rather than sent as `false`,
        // following ResourcesService.buildListParams' convention.
        includeArchived: this.includeArchived() ? true : undefined,
        search: this.searchText().trim() || undefined,
      })
      .subscribe({
        next: (result) => {
          if (requestId !== this.latestRequestId) {
            return;
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
