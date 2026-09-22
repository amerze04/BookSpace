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
import { AuthService } from '../../../../core/auth/auth.service';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../../resources/services/resources.service';
import { ResourceDetail } from '../../../resources/models/resources.models';
import { ResourceTypeIconComponent } from '../../../../shared/resource-type/resource-type-icon.component';
import { DecisionPanelComponent } from '../../../approvals/components/decision-panel/decision-panel.component';
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
  imports: [RouterLink, ResourceTypeIconComponent, DecisionPanelComponent],
  templateUrl: './booking-detail.component.html',
  styleUrl: './booking-detail.component.scss',
})
export class BookingDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly bookingsService = inject(BookingsService);
  private readonly recurrenceRulesService = inject(RecurrenceRulesService);
  private readonly resourcesService = inject(ResourcesService);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly auth = inject(AuthService);
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

  // **Who is reading this screen**, which it did not have to ask until
  // 2026-09-21. Phase 4 built this page for exactly one audience — the member
  // whose booking it is — and every second-person string on it was written in
  // that voice. Decision `0027` then made the page reachable by a second
  // audience, an approver reading a request on a resource they gate, and the
  // page had no way to tell them apart: an approver was told "the time is not
  // held for *you* yet" about someone else's request, and sent "back to *your*
  // calendar" for a booking that is not on it.
  //
  // `sub` is the caller's user id (decision `0009`'s claim shape), so the
  // comparison is free and needs no extra request.
  //
  // **A UI convenience only.** Every rule this gates is enforced server-side
  // independently — the cancel answers 404 to a non-owner whatever this says,
  // which is exactly the behaviour verified when `0027` landed. This makes the
  // screen honest, it does not make it safe.
  protected readonly viewerIsOwner = computed(() => {
    const booking = this.booking();
    const viewerUserId = this.auth.claims()?.sub;
    return booking !== null && viewerUserId !== undefined && booking.userId === viewerUserId;
  });

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
  //
  // **The three kinds describe the actor, not the reader**, and the template
  // picks the subject from `viewerIsOwner` — a split that mattered from
  // 2026-09-21, when decision `0027` gave this screen a second audience.
  // `'self'` means *the booking's owner cancelled it*, which the template used
  // to render unconditionally as "You cancelled this booking". Read by an
  // approver, that sentence was simply false. The kind is right; the pronoun
  // was the bug, so the fix is in the words rather than in this computed.
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
  //
  // **Ownership is checked first, and that arm is newer than the rest.** Until
  // decision `0027` (2026-09-21) only the booking's owner or a TenantAdmin
  // could reach this screen at all, so "can it be cancelled" and "may *I*
  // cancel it" were the same question and this computed only asked the first.
  // An approver can now open a request on a resource they gate — and their
  // reach widens what they may *read*, never what they may cancel (decision
  // `0002` keeps that with the owner and the TenantAdmin, and
  // `FindForCancelAsync` enforces it). Without this arm they would be offered a
  // "Cancel booking" button that answers 404 every time.
  //
  // A TenantAdmin reading someone else's booking is offered nothing here
  // either, which is a **deliberate under-offer**: they may genuinely cancel it
  // (decision `0002`), but nothing in WP-7's task list asks for an
  // administrator's cancellation UI, and inventing one is exactly the kind of
  // requirement CLAUDE.md §11 says to ask about rather than assume. Flagged in
  // `docs/wp7-plan.md` rather than silently built.
  protected readonly canCancel = computed(() => {
    const booking = this.booking();
    if (!booking || this.cancelled() || !this.viewerIsOwner()) {
      return false;
    }
    return (
      (booking.status === 'Pending' || booking.status === 'Confirmed')
      && Date.parse(booking.endsAtUtc) > this.loadedAtMs
    );
  });

  // **Whether to offer a decision on this screen** (owner's call, 2026-09-21 —
  // the queue row was not the only place an approver reaches for it).
  //
  // Two conditions:
  //   - the booking is still `Pending`, since a decided one has nothing to
  //     decide and the server would answer 422 BookingNotPending;
  //   - the viewer holds an approving role at all.
  //
  // **A third condition — "and the viewer is not the owner" — was here and was
  // wrong (removed 2026-09-22).** It was my own invention, justified in a
  // comment saying self-approval was "not a thing this UI should invite", and
  // nothing in the PRD, the FRs or any decision record ever asked for it. Three
  // things were true against it and none were checked at the time:
  //
  //   - **the backend allows it** — `ApprovalReach.ForResources` does not
  //     exclude the caller, and approving one's own request answers 200
  //     Confirmed (verified against the running API, 2026-09-22);
  //   - **the queue already allowed it**, rendering the panel on every row
  //     including the viewer's own — so the same shared panel refused on one
  //     screen and accepted on the other, which is precisely the drift a shared
  //     component was meant to prevent;
  //   - **an approver booking a resource they gate is ordinary.** They are
  //     often the person who knows the equipment best. Forcing them to find a
  //     second approver for their own booking is friction invented by a
  //     comment.
  //
  // Found by the owner walking the Phase 7 click-through, which is the seventh
  // time in this package that clicking found what the suite did not — and the
  // first where the suite was actively asserting the wrong behaviour.
  //
  // **The role check is a UI convenience and nothing more.** The real reach is
  // resource-scoped and lives server-side (`ApprovalReach`, decision `0018`);
  // the token only says which roles the caller holds, not which resources they
  // gate. So this can offer the panel to an Approver who does *not* gate this
  // resource — but only if they reached the booking at all, which decision
  // `0027` makes possible exactly when they do gate it. Where the two disagree
  // the server refuses and `approval-rejection.ts` explains it; that is the
  // right division, not a gap to paper over with a guess.
  protected readonly canDecide = computed(() => {
    const booking = this.booking();
    return (
      booking !== null && booking.status === 'Pending' && this.auth.canApproveBookings()
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
  //
  // **Ownership gates this too** (decision `0027`, 2026-09-21), for the same
  // reason `canCancel` gained the same arm: the series cancel is the booking
  // owner's or a TenantAdmin's, and an approver's read reach does not extend to
  // it. Offering it to an approver would be a button that cannot work.
  protected readonly canCancelSeries = computed(
    () => this.viewerIsOwner() && this.isRecurring() && this.seriesFreedCount() === null,
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

  // A decision on this screen re-reads the booking rather than patching it from
  // the response. The queue does the opposite — it drops the row and adjusts its
  // counts — and the difference is deliberate: a list wants to stay still while
  // it is worked down, but a *detail* screen exists to show the whole record,
  // and after a decision that record has genuinely changed in more places than
  // the decision response carries. `status` moves to Confirmed or Rejected, and
  // the approval section gains its decision, decider, timestamp and note — none
  // of which `{id, status, decidedByUserId, decidedAtUtc}` can supply in the
  // shape the screen renders.
  protected onDecided(): void {
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
