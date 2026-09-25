import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { UsersService } from '../../services/users.service';
import { DirectoryUser, UserRole } from '../../models/users.models';

// User management phase 6. Who is in this tenant.
//
// **The first screen in the application that answers that question at all.**
// Until phase 4 the only user read was the approvers picker's, which returns
// the decision `0018` eligible set — so an administrator who created a
// colleague through `POST /users` could not see them anywhere. This list is
// `scope=All`: Members too, and deactivated accounts, which is the whole point
// of the parameter.
//
// **Rows are not links, and that is a phase boundary rather than an
// oversight.** The user detail screen — roles, deactivate, reactivate — is
// phase 7. Linking to a route that does not exist yet is the mistake admin
// console phase 3 avoided when it left the approvers link pointing nowhere
// until the screen existed. What this screen does today is still the thing that
// was missing: see who is here, what they can do, and who has been switched
// off.
//
// Deliberately fewer controls than the admin resource list. No status filter:
// a tenant has tens of people, not hundreds of resources, and `GET /users` has
// no `isActive` parameter — filtering a fetched page client-side would leave
// `totalCount` and the page boundaries describing a different set than the rows
// under them, which is the mistake the resource list's own comment records.

const ADMIN_USER_PAGE_SIZE = 50;

// Same debounce as the other admin list: every keystroke is a real round trip,
// since search is a server-side `LIKE` over FullName and Email.
const SEARCH_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-admin-user-list',
  imports: [RouterLink],
  templateUrl: './admin-user-list.component.html',
  styleUrl: './admin-user-list.component.scss',
})
export class AdminUserListComponent {
  private readonly usersService = inject(UsersService);

  protected readonly searchText = signal('');
  private readonly searchRequestChanges$ = new Subject<string>();

  protected readonly items = signal<DirectoryUser[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  protected readonly page = signal(1);
  protected readonly totalPages = signal(1);
  protected readonly hasPreviousPage = signal(false);
  protected readonly hasNextPage = signal(false);

  // A newer request's result must not be overwritten by an older one landing
  // late — paging while a debounced search is in flight. Same guard as the
  // admin resource list.
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

  // Roles come back in the server's order, which is `User.Roles` over an owned
  // collection — stable enough to render, and sorted here anyway so two people
  // with the same roles read the same way down the list.
  protected sortedRoles(user: DirectoryUser): UserRole[] {
    return [...user.roles].sort();
  }

  private resetToFirstPageAndLoad(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    const requestId = ++this.latestRequestId;
    this.loading.set(true);
    this.loadError.set(false);

    this.usersService
      .listDirectory({
        page: this.page(),
        pageSize: ADMIN_USER_PAGE_SIZE,
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
