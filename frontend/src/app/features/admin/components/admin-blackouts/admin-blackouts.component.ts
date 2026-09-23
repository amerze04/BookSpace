import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { BreadcrumbService } from '../../../../layout/breadcrumb.service';
import { ResourcesService } from '../../../resources/services/resources.service';
import { BlackoutPeriodsService } from '../../../availability/services/blackout-periods.service';
import { BookingsService } from '../../../booking/services/bookings.service';
import { ResourceDetail } from '../../../resources/models/resources.models';
import {
  BlackoutPeriodSummary,
  CancelledBooking,
} from '../../../availability/models/blackout-periods.models';
import { BookingSummary } from '../../../booking/models/booking.models';
import { BlackoutRejection, describeBlackoutRejection } from '../../rejection/blackout-rejection';
import {
  BlackoutFormValue,
  MAX_REASON_LENGTH,
  hasErrors,
  resourceLocalToUtc,
  utcToResourceLocalInput,
  validateBlackout,
} from '../../blackouts/blackout-form';

// Admin console phase 6. Blackout periods — FR-3.4, decisions `0001` and `0019`.
//
// **The one admin screen that is genuinely per-row CRUD**, and the only place
// in this system where something is really deleted. Blackouts overlap freely,
// each is an independent fact about a period, and DELETE is a hard delete.
// `docs/admin-plan.md` §4.1 is explicit that making this look like the
// replace-the-set editors would misrepresent it, so it does not.
//
// **§4.4, settled here, and the answer is better than the plan expected.** The
// question was whether a confirmation could show which bookings a blackout will
// cancel, given the cascade happens inside the POST. It turns out the *response*
// already reports exactly what was cancelled (`CancelledBookingSummary`), and
// `GET /bookings` takes the same overlap window and resource filter the cascade
// uses. So the screen does both, and is careful about which is which:
//
//   before — a **preview**, from a real query, labelled as being decided at save
//            time. It can be stale by a booking created in between; that race is
//            documented in CLAUDE.md §6 and cannot be closed from a browser.
//   after  — the **record**, from the response. Authoritative.
//
// Saying only the first would be a promise the screen cannot keep. Saying only
// the second would mean an admin finds out what they cancelled afterwards.

type Mode = 'list' | 'create' | 'edit';

@Component({
  selector: 'app-admin-blackouts',
  imports: [RouterLink],
  templateUrl: './admin-blackouts.component.html',
  styleUrl: './admin-blackouts.component.scss',
})
export class AdminBlackoutsComponent {
  private readonly resourcesService = inject(ResourcesService);
  private readonly blackoutsService = inject(BlackoutPeriodsService);
  private readonly bookingsService = inject(BookingsService);
  private readonly route = inject(ActivatedRoute);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly maxReasonLength = MAX_REASON_LENGTH;

  // `protected`, not `private`, because the template reads it: admin console
  // phase 7 made the return leg a real `routerLink` rather than a click
  // handler, so the id has to be reachable from the markup.
  protected readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  protected readonly resource = signal<ResourceDetail | null>(null);
  protected readonly blackouts = signal<BlackoutPeriodSummary[]>([]);

  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);
  protected readonly notFound = signal(false);

  protected readonly mode = signal<Mode>('list');
  private readonly editingId = signal<string | null>(null);

  protected readonly startsAt = signal('');
  protected readonly endsAt = signal('');
  protected readonly reason = signal('');

  protected readonly submitting = signal(false);
  protected readonly rejection = signal<BlackoutRejection | null>(null);

  // The cascade's report, from the response. Null until a write lands.
  protected readonly cancelled = signal<CancelledBooking[] | null>(null);
  protected readonly cancelledFrom = signal<'created' | 'updated' | null>(null);

  // The preview: bookings that *would* be cancelled, as things stand.
  protected readonly preview = signal<BookingSummary[] | null>(null);
  protected readonly previewing = signal(false);
  protected readonly previewFailed = signal(false);

  protected readonly deletingId = signal<string | null>(null);
  protected readonly deleting = signal(false);

  protected readonly timeZoneId = computed(() => this.resource()?.timeZoneId ?? 'UTC');

  // FR-3.5: an archived resource accepts no writes at all, tidying included.
  protected readonly isArchived = computed(() => this.resource()?.isArchived ?? false);

  protected readonly formValue = computed<BlackoutFormValue>(() => ({
    startsAt: this.startsAt(),
    endsAt: this.endsAt(),
    reason: this.reason(),
  }));

  protected readonly errors = computed(() =>
    validateBlackout(this.formValue(), this.timeZoneId()),
  );

  protected readonly canSubmit = computed(
    () => !this.submitting() && !this.isArchived() && !hasErrors(this.errors()),
  );

  // A server field message outranks a client one until its control is edited —
  // the WP-7 convention, applied here too.
  protected readonly startsAtError = computed(
    () => this.rejection()?.fieldMessages.startsAt ?? this.errors().startsAt ?? null,
  );
  protected readonly endsAtError = computed(
    () => this.rejection()?.fieldMessages.endsAt ?? this.errors().endsAt ?? null,
  );
  protected readonly reasonError = computed(
    () => this.rejection()?.fieldMessages.reason ?? this.errors().reason ?? null,
  );

  constructor() {
    this.load();
    this.destroyRef.onDestroy(() => {
      this.breadcrumbService.setOverride(null);
      this.breadcrumbService.setInsertBeforeLast(null);
    });
  }

  // ---- The list ----

  protected startCreating(): void {
    this.mode.set('create');
    this.editingId.set(null);
    this.startsAt.set('');
    this.endsAt.set('');
    this.reason.set('');
    this.clearOutcome();
  }

  protected startEditing(blackout: BlackoutPeriodSummary): void {
    this.mode.set('edit');
    this.editingId.set(blackout.id);
    this.startsAt.set(utcToResourceLocalInput(blackout.startsAtUtc, this.timeZoneId()));
    this.endsAt.set(utcToResourceLocalInput(blackout.endsAtUtc, this.timeZoneId()));
    this.reason.set(blackout.reason ?? '');
    this.clearOutcome();
  }

  protected cancelForm(): void {
    this.mode.set('list');
    this.editingId.set(null);
    this.clearOutcome();
  }

  // ---- Field input ----

  protected onStartsAtInput(event: Event): void {
    this.startsAt.set((event.target as HTMLInputElement).value);
    this.onFormEdited();
  }

  protected onEndsAtInput(event: Event): void {
    this.endsAt.set((event.target as HTMLInputElement).value);
    this.onFormEdited();
  }

  protected onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
    this.rejection.set(null);
  }

  // ---- The preview (§4.4) ----

  // Asked for explicitly rather than run on every keystroke: it is a real
  // round trip over a window the admin is still typing, and a count that
  // flickers while somebody edits a date is worse than one they asked for.
  protected checkImpact(): void {
    if (hasErrors(this.errors())) {
      return;
    }

    this.previewing.set(true);
    this.previewFailed.set(false);

    const fromUtc = resourceLocalToUtc(this.startsAt(), this.timeZoneId());
    const toUtc = resourceLocalToUtc(this.endsAt(), this.timeZoneId());

    // `scope: 'tenant'` because a TenantAdmin has to see everyone's bookings,
    // not their own — the cascade does not care whose they are. The window is
    // the endpoint's own overlap filter, which matches the cascade's predicate
    // exactly (`EndsAtUtc > from && StartsAtUtc < to`).
    this.bookingsService
      .list({ resourceId: this.resourceId, from: fromUtc, to: toUtc, scope: 'tenant', pageSize: 100 })
      .subscribe({
        next: (page) => {
          const now = Date.now();

          // The cascade takes Pending and Confirmed only, and only what has not
          // already ended. Filtered here rather than server-side because
          // `ListBookingsQueryRequest.Status` takes one value, not a set — the
          // same constraint the calendar works around.
          this.preview.set(
            page.items.filter(
              (booking) =>
                (booking.status === 'Pending' || booking.status === 'Confirmed') &&
                new Date(booking.endsAtUtc).getTime() > now,
            ),
          );
          this.previewing.set(false);
        },
        error: () => {
          this.previewing.set(false);
          this.previewFailed.set(true);
        },
      });
  }

  // ---- Writes ----

  protected submit(): void {
    if (!this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.rejection.set(null);
    this.cancelled.set(null);

    const body = {
      startsAtUtc: resourceLocalToUtc(this.startsAt(), this.timeZoneId()),
      endsAtUtc: resourceLocalToUtc(this.endsAt(), this.timeZoneId()),
      reason: this.reason().trim() || null,
    };

    const editingId = this.editingId();

    const request$ =
      editingId === null
        ? this.blackoutsService.create(this.resourceId, body)
        : this.blackoutsService.update(this.resourceId, editingId, body);

    request$.subscribe({
      next: (response) => {
        this.submitting.set(false);
        this.mode.set('list');
        this.preview.set(null);

        // The record, from the response — not from the preview, which was only
        // ever a forecast.
        this.cancelled.set(response.cancelledBookings);
        this.cancelledFrom.set(editingId === null ? 'created' : 'updated');
        this.editingId.set(null);

        this.reloadBlackouts();
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const rejection = describeBlackoutRejection(error);
        this.rejection.set(rejection);

        if (rejection.resourceNotFound) {
          this.notFound.set(true);
        }
      },
    });
  }

  // ---- Delete ----

  protected startDeleting(id: string): void {
    this.deletingId.set(id);
    this.rejection.set(null);
  }

  protected cancelDeleting(): void {
    this.deletingId.set(null);
  }

  protected confirmDelete(): void {
    const id = this.deletingId();
    if (id === null || this.deleting()) {
      return;
    }

    this.deleting.set(true);
    this.rejection.set(null);

    this.blackoutsService.delete(this.resourceId, id).subscribe({
      next: () => {
        this.deleting.set(false);
        this.deletingId.set(null);
        this.cancelled.set(null);
        this.reloadBlackouts();
      },
      error: (error: unknown) => {
        this.deleting.set(false);
        this.rejection.set(describeBlackoutRejection(error));
      },
    });
  }

  protected blackoutBeingDeleted(): BlackoutPeriodSummary | null {
    const id = this.deletingId();
    return id === null ? null : (this.blackouts().find((b) => b.id === id) ?? null);
  }

  // ---- Rendering helpers ----

  // Both zones, always. An admin managing a room in another country needs the
  // room's clock to make the decision and their own to know when it is — the
  // confusion the WP-7 click-through surfaced, answered by showing both rather
  // than picking one.
  protected inResourceZone(utcIso: string): string {
    return new Intl.DateTimeFormat('en-GB', {
      timeZone: this.timeZoneId(),
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(utcIso));
  }

  protected inViewerZone(utcIso: string): string {
    return new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(
      new Date(utcIso),
    );
  }

  protected viewerZoneId(): string {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone;
    } catch {
      return 'your timezone';
    }
  }

  protected retryLoad(): void {
    this.load();
  }

  private onFormEdited(): void {
    this.rejection.set(null);
    // A forecast made for a different window is not a forecast for this one.
    this.preview.set(null);
    this.previewFailed.set(false);
  }

  private clearOutcome(): void {
    this.rejection.set(null);
    this.cancelled.set(null);
    this.cancelledFrom.set(null);
    this.preview.set(null);
    this.previewFailed.set(false);
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);

    forkJoin({
      resource: this.resourcesService.getById(this.resourceId),
      blackouts: this.blackoutsService.list(this.resourceId, { pageSize: 100, sort: 'startsAtUtc' }),
    }).subscribe({
      next: ({ resource, blackouts }) => {
        this.resource.set(resource);
        this.blackouts.set(blackouts.items);
        this.loading.set(false);
        this.breadcrumbService.setInsertBeforeLast(resource.name);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        if (describeBlackoutRejection(error).resourceNotFound) {
          this.notFound.set(true);
        } else {
          this.loadError.set(true);
        }
      },
    });
  }

  private reloadBlackouts(): void {
    this.blackoutsService
      .list(this.resourceId, { pageSize: 100, sort: 'startsAtUtc' })
      .subscribe({
        next: (page) => this.blackouts.set(page.items),
        error: () => this.loadError.set(true),
      });
  }
}
