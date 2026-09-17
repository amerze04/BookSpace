import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, distinctUntilChanged, filter, map, merge, of, switchMap } from 'rxjs';
import { BreadcrumbService } from '../../layout/breadcrumb.service';
import { ResourcesService } from '../resources/resources.service';
import { ResourceDetail, ResourceType } from '../resources/resources.models';
import { ResourceTypeIconComponent } from '../../shared/resource-type/resource-type-icon.component';
import { resourceCapacityLabel, resourceTypeLabel } from '../../shared/resource-type/resource-type';
import { BookingSelection, parseBookingSelection } from './booking-arrival';

type ResourceLoadResult = { kind: 'success'; resource: ResourceDetail } | { kind: 'error'; error: unknown };

// WP-7 Phase 3 step 2: the booking route's shell — the resource it is about,
// the slot it arrived with, and every failure mode around those. The form
// itself (one-off fields, the recurring half, submit and the outcome panels)
// lands in steps 3-7 inside the "Booking details" panel this renders.
//
// Loads the resource itself rather than trusting anything handed over by the
// previous screen, for the same reason AvailabilityComponent does: this route
// is reachable directly — a bookmarked URL, a refresh, a link pasted to a
// colleague — so it has to be able to stand up from nothing but its `:id`
// param and its query string.
@Component({
  selector: 'app-booking',
  imports: [RouterLink, ResourceTypeIconComponent],
  templateUrl: './booking.component.html',
  styleUrl: './booking.component.scss',
})
export class BookingComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly resourcesService = inject(ResourcesService);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly resource = signal<ResourceDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // Same reasoning as ResourceDetailComponent.notFound and
  // AvailabilityComponent's: AC-4 makes a nonexistent id and another tenant's
  // real one byte-identical, so this says neither "it doesn't exist" nor "it
  // isn't yours", and offers a way back rather than a retry that cannot help.
  protected readonly notFound = signal(false);

  // The slot the availability screen sent here, as query parameters —
  // `?startUtc=…&endUtc=…&quantity=…` (booking-arrival.ts owns both halves of
  // that contract and explains why it is the URL rather than router state).
  //
  // Observed, not read once: the query string can change under a component the
  // router is reusing — a second "Continue to booking" for a different slot on
  // the same resource does exactly that — and a one-time read would then leave
  // the form showing the previous slot. Same "don't trust a one-time read"
  // lesson the `:id` pipeline below already applies.
  //
  // Null is an ordinary state, not a failure: a bare deep link has no
  // selection, and per the call settled before this phase started
  // (wp7-plan.md Phase 3) the one-off path is pre-fill only, so that renders a
  // panel pointing back at the availability screen rather than a date/time
  // picker, which would need a local->UTC inversion this client deliberately
  // does not own (CLAUDE.md §4.3).
  protected readonly selection = toSignal<BookingSelection | null>(
    this.route.queryParamMap.pipe(map((params) => parseBookingSelection(params))),
    { initialValue: null },
  );

  // An archived resource cannot be booked at all (ResourceArchived), so it
  // short-circuits both the form and the arrival state — the same
  // "Archived — not bookable" treatment the list and detail screens already
  // give in place of their own booking CTAs, rather than a form that could
  // only ever be refused on submit.
  protected readonly isArchived = computed(() => this.resource()?.isArchived ?? false);

  private resourceId: string;

  private readonly retry$ = new Subject<void>();

  constructor() {
    const initialId = this.route.snapshot.paramMap.get('id');
    if (!initialId) {
      throw new Error('BookingComponent requires an "id" route param.');
    }
    this.resourceId = initialId;

    // The same route-id-keyed switchMap pipeline ResourceDetailComponent and
    // AvailabilityComponent both use since the 2026-09-16 frontend hardening
    // pass: a route-id change or a retry cancels the fetch still in flight
    // instead of leaving a stale response able to land after a newer one.
    const idChanges$ = this.route.paramMap.pipe(
      map((params) => params.get('id')),
      filter((id): id is string => !!id),
      distinctUntilChanged(),
    );

    merge(idChanges$, this.retry$.pipe(map(() => this.resourceId)))
      .pipe(
        map((id) => {
          this.resourceId = id;
          return id;
        }),
        switchMap((id) => this.fetchResource$(id)),
        takeUntilDestroyed(),
      )
      .subscribe((result) => this.applyResult(result));

    // Never leave this resource's name in the breadcrumb of whatever page
    // comes next.
    this.destroyRef.onDestroy(() => this.breadcrumbService.setInsertBeforeLast(null));
  }

  protected retry(): void {
    this.retry$.next();
  }

  protected typeLabel(type: ResourceType): string {
    return resourceTypeLabel(type);
  }

  protected capacityLabel(resource: ResourceDetail): string {
    return resourceCapacityLabel(resource);
  }

  protected approvalLabel(resource: ResourceDetail): string {
    return resource.requiresApproval ? 'Approval required' : 'Instant confirmation';
  }

  private fetchResource$(id: string): Observable<ResourceLoadResult> {
    this.loading.set(true);
    this.loadError.set(false);
    this.notFound.set(false);
    this.breadcrumbService.setInsertBeforeLast(null);

    return this.resourcesService.getById(id).pipe(
      map((resource) => ({ kind: 'success' as const, resource })),
      catchError((error: unknown) => of({ kind: 'error' as const, error })),
    );
  }

  private applyResult(result: ResourceLoadResult): void {
    this.loading.set(false);
    if (result.kind === 'error') {
      if (result.error instanceof HttpErrorResponse && result.error.status === 404) {
        this.notFound.set(true);
      } else {
        this.loadError.set(true);
      }
      return;
    }

    this.resource.set(result.resource);

    // insertBeforeLast, not override: `:id/book` is a *sibling* of `:id`, so
    // its own route-title chain is only ['Resources', 'Book resource'] — there
    // is no crumb standing for the resource to replace, only one to splice in
    // (BreadcrumbService's own comment, written for `:id/availability`'s
    // identical case).
    this.breadcrumbService.setInsertBeforeLast(result.resource.name);
  }
}
