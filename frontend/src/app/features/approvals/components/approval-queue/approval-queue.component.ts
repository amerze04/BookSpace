import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BookingsService } from '../../../booking/services/bookings.service';
import { BookingSummary } from '../../../booking/models/booking.models';
import {
  APPROVAL_QUEUE_PAGE_SIZE,
  ApprovalQueueRow,
  toQueueRow,
} from '../../queue/approval-queue';

// WP-7 Phase 6 step 3 — the approval queue, FR-7.1–FR-7.5 and WP-7's last
// unbuilt screen.
//
// **The request this screen makes is the whole design decision**, and it is one
// request, not two: `GET /bookings?scope=tenant&status=Pending`. An Approver and
// a TenantAdmin send exactly the same thing, and the server narrows the rows
// itself — `ApprovalReach` is unrestricted for an admin and
// assigned-resources-only for an approver (decision `0018`). **This component
// does not branch on role and must not start.** Doing so would put a second,
// client-side copy of an authorization rule next to the real one, which is the
// shape CLAUDE.md §4.2 rejects one scope down; and it would be a copy that
// cannot see what the server sees, since which resources an approver gates is
// not in the token.
//
// `approverGuard` already keeps this route and its nav item away from anyone who
// cannot approve, so the "plain member" case — an empty page rather than a 403 —
// is not a state this screen has to render.
//
// **Oldest first**, `?sort=createdAtUtc`: a queue's default order is the order
// people have been waiting in. That is also the only ordering the requested-at
// column can justify, and the reason step 1 added `createdAtUtc` to the list row
// — the endpoint accepted the sort long before it returned the field.
//
// No decision controls yet; approve and reject are step 4. This step is the
// read, its three states and its paging.
@Component({
  selector: 'app-approval-queue',
  imports: [RouterLink],
  templateUrl: './approval-queue.component.html',
  styleUrl: './approval-queue.component.scss',
})
export class ApprovalQueueComponent {
  private readonly bookingsService = inject(BookingsService);

  protected readonly viewerTimeZoneId = Intl.DateTimeFormat().resolvedOptions().timeZone;

  // Read once, when the component is created, rather than ticking — the same
  // trade the booking detail screen makes for its own "now". A queue is read
  // and acted on in a sitting; a "Waiting 3 hours" label that silently became
  // "Waiting 4 hours" while nobody looked would cost a timer, a zoneless-safe
  // signal write and a re-render, to correct a number that was never load-
  // bearing. Every fetch re-reads it (see load), so paging or retrying gives a
  // fresh reading without one.
  private nowMs = Date.now();

  private readonly bookings = signal<BookingSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // Paged off the server's own PagedResult fields, never recomputed here —
  // `core/http/paged-result.ts` mirrors them precisely so there is one source
  // for them rather than two that could disagree.
  protected readonly page = signal(1);
  protected readonly totalCount = signal(0);
  protected readonly totalPages = signal(1);
  protected readonly hasPreviousPage = signal(false);
  protected readonly hasNextPage = signal(false);

  // The rows the template renders, already in words. `nowMs` is read inside the
  // computed rather than captured, so a fetch that refreshes it refreshes the
  // waiting labels with it.
  protected readonly rows = computed<ApprovalQueueRow[]>(() =>
    this.bookings().map((booking) => toQueueRow(booking, this.viewerTimeZoneId, this.nowMs)),
  );

  // Guards against a stale response overwriting a newer one — a fast
  // Previous/Next double-click is enough to produce one. Same idea as
  // ResourceListComponent's own counter.
  private latestRequestId = 0;

  constructor() {
    this.load();
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

  private load(): void {
    const requestId = ++this.latestRequestId;
    this.loading.set(true);
    this.loadError.set(false);
    this.nowMs = Date.now();

    this.bookingsService
      .list({
        scope: 'tenant',
        status: 'Pending',
        sort: 'createdAtUtc',
        page: this.page(),
        pageSize: APPROVAL_QUEUE_PAGE_SIZE,
      })
      .subscribe({
        next: (result) => {
          if (requestId !== this.latestRequestId) {
            return; // a newer request already landed; this one is stale
          }
          this.bookings.set(result.items);
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
