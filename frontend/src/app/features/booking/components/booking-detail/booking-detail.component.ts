import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, distinctUntilChanged, filter, map, merge, of, switchMap } from 'rxjs';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../../resources/services/resources.service';
import { ResourceDetail } from '../../../resources/models/resources.models';
import { ResourceTypeIconComponent } from '../../../../shared/resource-type/resource-type-icon.component';
import { resourceTypeLabel } from '../../../../shared/resource-type/resource-type';
import {
  SpanLabels,
  formatDurationWords,
  instantLabel,
  spanLabels,
} from '../../../availability/date/local-date';
import { BookingsService } from '../../services/bookings.service';
import { RecurrenceRulesService } from '../../services/recurrence-rules.service';
import {
  BookingDetail,
  BookingStatus,
  MAX_CANCELLATION_REASON_LENGTH,
} from '../../models/booking.models';
import { BookingRejection } from '../../rejection/booking-rejection';
import {
  describeCancelRejection,
  describeSeriesCancelRejection,
} from '../../rejection/cancel-rejection';

// Which of the two cancellations a confirmation is about. FR-5.3 requires both
// to be reachable and neither to be implied by the other.
type CancelMode = 'occurrence' | 'series';

type BookingLoadResult =
  | { kind: 'success'; booking: BookingDetail }
  | { kind: 'notFound' }
  | { kind: 'error' };

// The booking DTOs spell the interval `startsAtUtc`/`endsAtUtc` while the slot
// contract and `spanLabels` use `startUtc`/`endUtc`. Mapped at the call site,
// as the booking form already does, rather than teaching the shared formatter
// both spellings.
function spanOf(booking: BookingDetail): { startUtc: string; endUtc: string } {
  return { startUtc: booking.startsAtUtc, endUtc: booking.endsAtUtc };
}

// WP-7 Phase 4 step 4 — one booking, read back in full.
//
// **Its own route rather than a panel on the calendar** (`/bookings/:id`):
// FR-5.2 asks that each occurrence of a series be independently viewable, and a
// booking worth discussing is worth linking to — the same instinct that put the
// selected slot in the URL in Phase 3.
//
// **It must render a cancelled booking honestly even though the calendar will
// never route anyone to one.** The status rules mean a `Cancelled` or
// `Rejected` booking is not drawn on the grid, so the only ways here are a
// direct link, a bookmark, or the booking screen's own "check your calendar"
// message — all of which still resolve. A screen that assumed its subject was
// live would show nothing useful at exactly the moment someone is trying to
// find out what happened.
@Component({
  selector: 'app-booking-detail',
  imports: [RouterLink, ResourceTypeIconComponent],
  templateUrl: './booking-detail.component.html',
  styleUrl: './booking-detail.component.scss',
})
export class BookingDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly bookingsService = inject(BookingsService);
  private readonly recurrenceRulesService = inject(RecurrenceRulesService);
  private readonly resourcesService = inject(ResourcesService);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly host = inject(ElementRef<HTMLElement>);

  protected readonly booking = signal<BookingDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // Distinct from a generic failure, and worded so it says neither "it doesn't
  // exist" nor "it isn't yours": `GET /bookings/{id}` answers 404 identically
  // for another member's booking, another tenant's, and an id that exists
  // nowhere — AC-4's rule applied inside one tenant. Mirroring that wording is
  // the point, not a vagueness to tidy up.
  protected readonly notFound = signal(false);

  // The resource is a *second*, best-effort fetch, and its failure is
  // deliberately silent. The booking response carries `resourceName` but no
  // `timeZoneId`, so without this the screen cannot say what the span means on
  // the room's own clock — but everything else on the page is still true and
  // useful, so a failed resource read degrades one line rather than the screen.
  // Same reasoning as the availability screen's blackout fetch.
  protected readonly resource = signal<ResourceDetail | null>(null);

  protected readonly viewerTimeZoneId = Intl.DateTimeFormat().resolvedOptions().timeZone;

  private bookingId: string;
  private readonly retry$ = new Subject<void>();

  // Read once, when the component is created, rather than ticking — see
  // `canCancel` for why a stale "now" is the right trade here.
  private readonly loadedAtMs = Date.now();

  // **The viewer's own zone leads here**, which is the opposite emphasis from
  // the booking *form* one screen back — deliberately. Decision `0003` governs
  // the availability question ("Monday 9am" is what the room's clock says), so
  // the form led with the resource's zone: that is the reading the member chose
  // against. Reading a booking back is the ordinary calendar case
  // (wp7-plan.md §3), where what a person wants is when to actually turn up.
  protected readonly viewerSpan = computed<SpanLabels | null>(() => {
    const booking = this.booking();
    return booking ? spanLabels(spanOf(booking), this.viewerTimeZoneId) : null;
  });

  // Shown only when the two genuinely differ, and only once the resource has
  // loaded — a member in Sarajevo with a New York room needs both readings, and
  // one in the same zone would just be told the same thing twice.
  protected readonly resourceSpan = computed<SpanLabels | null>(() => {
    const booking = this.booking();
    const resource = this.resource();
    if (!booking || !resource || resource.timeZoneId === this.viewerTimeZoneId) {
      return null;
    }
    return spanLabels(spanOf(booking), resource.timeZoneId);
  });

  protected readonly durationLabel = computed(() => {
    const booking = this.booking();
    if (!booking) {
      return '';
    }
    return formatDurationWords(
      Math.round((Date.parse(booking.endsAtUtc) - Date.parse(booking.startsAtUtc)) / 60_000),
    );
  });

  protected readonly isRecurring = computed(() => this.booking()?.recurrenceRuleId !== null);

  // Three readings of the cancellation trio, not one generic "Cancelled" — the
  // distinction is the whole reason `CancelledByUserId` is recorded separately
  // from `UserId` (decision `0002`).
  //
  // A **null** actor beside a real reason is the third case and the one most
  // easily mistaken for missing data: `Booking.CancelForBlackout` leaves it null
  // on purpose, because a blackout cascade has no person behind it, and the
  // reason carries decision `0019`'s text snapshot naming the blackout.
  protected readonly cancellation = computed<
    { kind: 'self' | 'administrator' | 'blackout'; reason: string | null; at: string } | null
  >(() => {
    const booking = this.booking();
    if (!booking || booking.cancelledAtUtc === null) {
      return null;
    }

    const kind =
      booking.cancelledByUserId === null
        ? 'blackout'
        : booking.cancelledByUserId === booking.userId
          ? 'self'
          : 'administrator';

    return {
      kind,
      reason: booking.cancellationReason,
      at: instantLabel(booking.cancelledAtUtc, this.viewerTimeZoneId),
    };
  });

  // ---- Step 5: cancelling ----

  // Which confirmation is open, if any. A mode rather than a boolean because
  // the two cancellations are genuinely different acts with different reach,
  // and the panel has to say which one it is about to do.
  protected readonly confirmMode = signal<CancelMode | null>(null);
  protected readonly cancelling = signal(false);
  protected readonly reason = signal('');
  protected readonly cancelRejection = signal<BookingRejection | null>(null);
  protected readonly cancelled = signal(false);

  // How many occurrences the series cancel actually freed — from
  // `cancelledBookingIds`, which the endpoint returns as ids rather than a count
  // precisely so a client knows *which*. Null until a series cancel succeeds.
  protected readonly seriesFreedCount = signal<number | null>(null);

  // `Booking.CanBeCancelled` mirrored: not terminal **and** not already ended.
  // The second half is on `EndsAtUtc` rather than `StartsAtUtc` on purpose — a
  // meeting already under way can still be called off, because the room is free
  // from then on, which is the whole point.
  //
  // **The server stays the authority and this is only about whether to offer
  // the action.** `now` is read when the booking loads rather than ticking, so
  // a booking that ends while the screen sits open still shows the button; the
  // request then answers `422 BookingNotCancellable` and the dialect above
  // explains it. That is the right failure mode — the alternative is a button
  // vanishing under the pointer.
  protected readonly canCancel = computed(() => {
    const booking = this.booking();
    if (!booking || this.cancelled()) {
      return false;
    }
    return (
      (booking.status === 'Pending' || booking.status === 'Confirmed')
      && Date.parse(booking.endsAtUtc) > this.loadedAtMs
    );
  });

  // A series occurrence's button says **which** it cancels, rather than being a
  // single ambiguous "Cancel booking" on a booking that belongs to eleven
  // others (FR-5.3).
  protected readonly cancelActionLabel = computed(() =>
    this.isRecurring() ? 'Cancel this occurrence' : 'Cancel booking',
  );

  // **Offered on a different rule from the occurrence's, and the client cannot
  // check it.** `RecurrenceRule.CanBeCancelled()` is `Status == Active` and
  // nothing else — no time component — so a series with future occurrences is
  // cancellable even when *this* occurrence is in the past or already cancelled.
  // The two actions therefore appear independently.
  //
  // `GetBookingQueryResponse` carries `recurrenceRuleId` but not the rule's
  // status, and there is no `GET /recurrence-rules/{id}` to ask (wp7-plan.md
  // notes the same gap for the idempotency-key question). So this offers the
  // action optimistically and lets `422 RecurrenceRuleNotCancellable` say the
  // series is already cancelled — the same "server is the authority" trade
  // `canCancel` makes about a stale clock, for a stronger reason: here the
  // client has no way to know at all.
  protected readonly canCancelSeries = computed(
    () => this.isRecurring() && this.seriesFreedCount() === null,
  );

  protected readonly confirmHeading = computed(() =>
    this.confirmMode() === 'series' ? 'Cancel the whole remaining series?' : `${this.cancelActionLabel()}?`,
  );

  protected readonly confirmActionLabel = computed(() =>
    this.confirmMode() === 'series' ? 'Yes, cancel the series' : 'Yes, cancel it',
  );

  // Mirrors `Bookings.CancellationReason NVARCHAR(300)` so the box bounds the
  // input rather than letting a 400 be the first thing that says so.
  protected readonly maxReasonLength = MAX_CANCELLATION_REASON_LENGTH;

  protected readonly reasonError = computed(() =>
    this.reason().length > MAX_CANCELLATION_REASON_LENGTH
      ? `Keep this under ${MAX_CANCELLATION_REASON_LENGTH} characters.`
      : null,
  );

  protected readonly approvalRequestedAt = computed(() => this.instant(this.booking()?.approval?.requestedAtUtc));
  protected readonly approvalExpiresAt = computed(() => this.instant(this.booking()?.approval?.expiresAtUtc));
  protected readonly approvalDecidedAt = computed(() => this.instant(this.booking()?.approval?.decidedAtUtc));
  protected readonly createdAt = computed(() => this.instant(this.booking()?.createdAtUtc));
  protected readonly updatedAt = computed(() => this.instant(this.booking()?.updatedAtUtc));

  constructor() {
    const initialId = this.route.snapshot.paramMap.get('id');
    if (!initialId) {
      throw new Error('BookingDetailComponent requires an "id" route param.');
    }
    this.bookingId = initialId;

    // The same route-id-keyed switchMap pipeline every other detail screen in
    // this app uses since the 2026-09-16 hardening pass: a stale fetch is
    // cancelled outright rather than merely ignored on arrival.
    const idChanges$ = this.route.paramMap.pipe(
      map((params) => params.get('id')),
      filter((id): id is string => !!id),
      distinctUntilChanged(),
    );

    merge(idChanges$, this.retry$.pipe(map(() => this.bookingId)))
      .pipe(
        map((id) => {
          this.bookingId = id;
          this.loading.set(true);
          this.loadError.set(false);
          this.notFound.set(false);
          this.resource.set(null);
          return id;
        }),
        switchMap((id) => this.fetchBooking$(id)),
        takeUntilDestroyed(),
      )
      .subscribe((result) => this.applyResult(result));

    this.destroyRef.onDestroy(() => this.breadcrumbService.setOverride(null));
  }

  protected retry(): void {
    this.retry$.next();
  }

  // **Focus follows the disclosure, in both directions.** Opening the panel
  // moves focus onto its heading — which is what says *which* cancellation is
  // about to happen, and a keyboard or screen-reader user who is left on the
  // trigger hears nothing about the panel that just appeared. Backing out
  // returns focus to the button they came from, rather than dropping it on
  // `<body>` and sending them back to the top of the page.
  protected startConfirming(mode: CancelMode): void {
    this.confirmMode.set(mode);
    this.cancelRejection.set(null);
    this.focusAfterRender('.confirm-heading');
  }

  protected stopConfirming(): void {
    const mode = this.confirmMode();
    this.confirmMode.set(null);
    this.reason.set('');
    this.cancelRejection.set(null);
    this.focusAfterRender(mode === 'series' ? '.danger-outline-button' : '.danger-button');
  }

  // `afterNextRender`, not an immediate call: the element being focused does
  // not exist until the template has reacted to the signal that was just
  // written. Same reasoning the availability screen's scroll-into-view uses.
  private focusAfterRender(selector: string): void {
    afterNextRender(
      () => (this.host.nativeElement.querySelector(selector) as HTMLElement | null)?.focus(),
      { injector: this.injector },
    );
  }

  protected onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
    // A server message about the reason is cleared as soon as the control it
    // belongs to is edited — otherwise it could only be cleared by the submit
    // it is blocking. Same precedence the recurring form settled on in the
    // 2026-09-17 hardening pass.
    if (this.cancelRejection()?.fieldMessages.reason) {
      this.cancelRejection.set(null);
    }
  }

  // **Nothing retries this, anywhere.** `POST .../cancel` is deliberately not
  // idempotent, so a repeat either answers 422 or rewrites who cancelled it —
  // see `cancel-rejection.ts`. The button is disabled while the request is in
  // flight and this re-checks the same guard for anything reaching it
  // programmatically.
  protected confirmCancel(): void {
    const booking = this.booking();
    if (!booking || this.cancelling() || this.reasonError() !== null) {
      return;
    }

    const mode = this.confirmMode();
    if (mode === null) {
      return;
    }

    this.cancelling.set(true);
    this.cancelRejection.set(null);

    const raw = this.reason().trim();
    const reason = raw === '' ? null : raw;

    if (mode === 'series') {
      this.cancelSeries(booking, reason);
      return;
    }

    this.bookingsService
      .cancel(booking.id, { reason })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.cancelling.set(false);
          this.confirmMode.set(null);
          this.cancelled.set(true);
          this.applyCancellation(response);
          // The panel the member was standing in has just been replaced by the
          // outcome, so focus moves to what replaced it rather than falling to
          // <body> and sending them back to the top of the page.
          this.focusAfterRender('.cancel-done');
        },
        error: (error: unknown) => {
          this.cancelling.set(false);
          this.cancelRejection.set(describeCancelRejection(error));
        },
      });
  }

  private cancelSeries(booking: BookingDetail, reason: string | null): void {
    // Guarded by the template, but re-checked here for anything reaching this
    // programmatically — the same discipline `confirmCancel` applies.
    if (booking.recurrenceRuleId === null) {
      this.cancelling.set(false);
      return;
    }

    this.recurrenceRulesService
      .cancel(booking.recurrenceRuleId, { reason })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.cancelling.set(false);
          this.confirmMode.set(null);
          this.seriesFreedCount.set(response.cancelledBookingIds.length);
          this.focusAfterRender('.cancel-done');

          // **This booking is only cancelled if the response says it was.** The
          // series cancel reaches occurrences with `EndsAtUtc > now` and leaves
          // past ones alone, so a member looking at a finished occurrence when
          // they cancel the series watches the rest go while this one stays —
          // which is correct, and would read as a bug if the screen crossed it
          // out anyway.
          if (response.cancelledBookingIds.includes(booking.id)) {
            this.cancelled.set(true);
            this.applyCancellation({
              status: 'Cancelled',
              cancelledByUserId: response.cancelledByUserId,
              cancelledAtUtc: response.cancelledAtUtc,
              cancellationReason: reason,
            });
          }
        },
        error: (error: unknown) => {
          this.cancelling.set(false);
          this.cancelRejection.set(describeSeriesCancelRejection(error));
        },
      });
  }

  // Updated from the response rather than re-fetching: it carries the
  // cancellation trio and the freed interval precisely so a client does not
  // have to ask again.
  //
  // The **one** thing it does not carry is what became of the approval request,
  // and leaving that showing "Pending" on a cancelled booking would be a
  // visible lie. `ApprovalRequest.Withdraw` turns a still-pending request into
  // `Withdrawn` whenever its booking is cancelled — verified against the live
  // API, not assumed — so that transition is mirrored here, the same way
  // `canCancel` mirrors `CanBeCancelled`.
  private applyCancellation(response: {
    status: BookingStatus;
    cancelledByUserId: string;
    cancelledAtUtc: string;
    cancellationReason: string | null;
  }): void {
    const booking = this.booking();
    if (!booking) {
      return;
    }

    this.booking.set({
      ...booking,
      status: response.status,
      cancelledByUserId: response.cancelledByUserId,
      cancelledAtUtc: response.cancelledAtUtc,
      cancellationReason: response.cancellationReason,
      approval:
        booking.approval && booking.approval.decision === 'Pending'
          ? { ...booking.approval, decision: 'Withdrawn', decidedAtUtc: response.cancelledAtUtc }
          : booking.approval,
    });
  }

  protected statusLabel(status: BookingStatus): string {
    return status === 'NoShow' ? 'No-show' : status;
  }

  protected typeLabel(resource: ResourceDetail): string {
    return resourceTypeLabel(resource.resourceType);
  }

  private instant(utcIso: string | null | undefined): string | null {
    return utcIso ? instantLabel(utcIso, this.viewerTimeZoneId) : null;
  }

  private applyResult(result: BookingLoadResult): void {
    this.loading.set(false);

    if (result.kind === 'notFound') {
      this.notFound.set(true);
      this.booking.set(null);
      return;
    }

    if (result.kind === 'error') {
      this.loadError.set(true);
      this.booking.set(null);
      return;
    }

    this.booking.set(result.booking);
    // The booking's own name where it has one, the resource's otherwise —
    // matching what a calendar chip showed, so arriving here reads as opening
    // the thing that was clicked rather than something else.
    this.breadcrumbService.setOverride(result.booking.title?.trim() || result.booking.resourceName);
    this.fetchResource(result.booking.resourceId);
  }

  private fetchBooking$(id: string): Observable<BookingLoadResult> {
    return this.bookingsService.getById(id).pipe(
      map((booking) => ({ kind: 'success', booking }) as BookingLoadResult),
      catchError((error: unknown) =>
        of<BookingLoadResult>(
          error instanceof HttpErrorResponse && error.status === 404
            ? { kind: 'notFound' }
            : { kind: 'error' },
        ),
      ),
    );
  }

  private fetchResource(resourceId: string): void {
    this.resourcesService
      .getById(resourceId)
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((resource) => {
        // Guarded rather than assigned blindly: this is a second request racing
        // a possible id change, and `switchMap` above only governs the booking
        // fetch. Without the check, a slow resource read for the previous
        // booking could land after a newer booking had already rendered.
        if (resource && resource.id === this.booking()?.resourceId) {
          this.resource.set(resource);
        }
      });
  }
}
