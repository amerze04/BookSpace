import { Component, DestroyRef, HostListener, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, distinctUntilChanged, filter, map, merge, of, switchMap } from 'rxjs';
import { BreadcrumbService } from '../../layout/breadcrumb.service';
import { ResourcesService } from '../resources/resources.service';
import { ResourceDetail } from '../resources/resources.models';
import { ResourceTypeIconComponent } from '../../shared/resource-type/resource-type-icon.component';
import { resourceCapacityLabel, resourceTypeLabel } from '../../shared/resource-type/resource-type';
import { AvailabilityService } from './availability.service';
import { AvailabilityResponse, LocalDateString } from './availability.models';
import {
  addDays,
  formatLocalDate,
  formatLocalDateWithFullWeekday,
  formatLocalDateWithWeekday,
  formatLocalDateWithWeekdayAndYear,
  formatMinutesOfDay,
  rangeLengthDays,
  resourceLocalToday,
  weekdayOf,
} from './local-date';
import {
  DaySegment,
  buildAxisTicks,
  buildDayRows,
  clamp,
  computeAxis,
  datesInRange,
  minuteSpanStylePercent,
  resourceLocalMinutesToUtc,
} from './availability-grid';

// Mirrors AvailabilityQueryRules.MaxRangeDays on the backend exactly — the
// point is to refuse an over-long range in the UI *before* the request goes
// out, not to invent a different number the server would then reject anyway.
const MAX_RANGE_DAYS = 90;

// The rolling window's width when the picker first opens or "Today"/the
// arrows reset it — matches the design's own default view (Sep 21-27, a
// seven-day span). Not itself a backend rule; purely this screen's own
// presentation default.
const DEFAULT_WINDOW_DAYS = 7;

// The Start/End time dropdowns' granularity (step 6) — matches the design's
// own example times (09:15, 11:30), and a round number is easier to scan in
// a dropdown than an arbitrary one. Purely a presentation/interaction
// choice, never a business rule: nothing in the PRD or work packages ties
// booking granularity to 15 minutes, and decisions/0011-adjacent backend
// semantics allow whole-second precision. Item 5's fix keeps this constant
// scoped to what it actually governs (dropdown steps, drag snapping, and —
// via effectiveMinDuration below — keeping those dropdowns' own bounds
// non-degenerate) and out of anywhere that asserts what the *resource*
// requires; see durationError's own comment for where that line is drawn.
const TIME_OPTION_STEP_MINUTES = 15;

// A bound for the Start/End dropdowns' own option ranges, not a claim about
// what the resource requires — used only to stop those two dropdowns from
// offering a combination that would leave no room for a nonzero, step-
// aligned duration. `null` (no configured minimum) falls back to one step
// for exactly that mechanical reason, never as a stand-in minimum duration:
// durationError below checks the resource's real minDurationMinutes
// directly, so a resource with none configured can never see a false
// "requires at least 15 minutes" message this function's own fallback would
// otherwise imply.
function effectiveMinDuration(resource: ResourceDetail): number {
  return resource.minDurationMinutes ?? TIME_OPTION_STEP_MINUTES;
}

function effectiveMaxDuration(resource: ResourceDetail): number {
  return resource.maxDurationMinutes ?? Number.POSITIVE_INFINITY;
}

type ResourceLoadResult = { kind: 'success'; resource: ResourceDetail } | { kind: 'error'; error: unknown };

function validateRange(from: LocalDateString, to: LocalDateString): string | null {
  if (!from || !to) {
    return 'Both dates are required.';
  }
  // ISO Y-M-D strings compare lexicographically in calendar order, so this
  // needs no date parsing.
  if (to < from) {
    return 'The end date must not be before the start date.';
  }
  if (rangeLengthDays(from, to) > MAX_RANGE_DAYS) {
    return `The range must not exceed ${MAX_RANGE_DAYS} days.`;
  }
  return null;
}

// Every TIME_OPTION_STEP_MINUTES mark from `fromMinutes` up to (but not
// necessarily aligned with) `toMinutesInclusive`, with `toMinutesInclusive`
// itself always appended as the final option even when it doesn't fall on
// that step — a segment's own bounds can land on an odd offset (a blackout
// cutting a window doesn't respect any particular granularity), so the exact
// boundary always has to be reachable regardless of alignment.
function buildTimeOptions(fromMinutes: number, toMinutesInclusive: number): { minutes: number; label: string }[] {
  const minutes: number[] = [];
  for (let m = fromMinutes; m < toMinutesInclusive; m += TIME_OPTION_STEP_MINUTES) {
    minutes.push(m);
  }
  minutes.push(toMinutesInclusive);
  return minutes.map((m) => ({ minutes: m, label: formatMinutesOfDay(m) }));
}

// "2 hours 15 minutes" / "2 hours" / "45 minutes" — the selected-time
// summary panel's own duration phrasing (full words, unlike the resource
// detail page's abbreviated "2 hours 15 min", to match the design exactly).
function formatSelectionDuration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  const parts: string[] = [];
  if (hours > 0) {
    parts.push(`${hours} ${hours === 1 ? 'hour' : 'hours'}`);
  }
  if (mins > 0) {
    parts.push(`${mins} ${mins === 1 ? 'minute' : 'minutes'}`);
  }
  return parts.length > 0 ? parts.join(' ') : '0 minutes';
}

// WP-7 Phase 2. Step 3 built the shell, step 4 the date-range/quantity
// controls. This step (5) wires them to AvailabilityService and renders the
// bookable-interval grid: a shared hour axis, one row per visible day, and
// bars positioned by resource-local time — the segment-splitting and axis
// math itself lives in availability-grid.ts, kept out of this file since
// none of it is Angular-specific.
//
// Loads the resource the same way ResourceDetailComponent does (getById,
// same 404-vs-other-error split, same "observe paramMap rather than snapshot
// once" reasoning) rather than trusting router state passed forward from the
// resource list/detail screens — this route is reachable directly (a bare
// URL, a refresh), so it has to be able to load itself from nothing but the
// `:id` param.
@Component({
  selector: 'app-availability',
  imports: [RouterLink, ResourceTypeIconComponent],
  templateUrl: './availability.component.html',
  styleUrl: './availability.component.scss',
})
export class AvailabilityComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly resourcesService = inject(ResourcesService);
  private readonly availabilityService = inject(AvailabilityService);
  private readonly breadcrumbService = inject(BreadcrumbService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly resource = signal<ResourceDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  // Same reasoning as ResourceDetailComponent.notFound: AC-4 means a
  // nonexistent id and another tenant's real one are byte-identical, so
  // retrying the same id can't help — this state offers a way back instead.
  protected readonly notFound = signal(false);

  // Empty until the resource has loaded once (its timezone decides what
  // "today" even means) — see initializeRangeIfUnset.
  protected readonly fromDate = signal<LocalDateString>('');
  protected readonly toDate = signal<LocalDateString>('');
  protected readonly quantity = signal(1);

  protected readonly showRangePicker = signal(false);
  protected readonly draftFrom = signal<LocalDateString>('');
  protected readonly draftTo = signal<LocalDateString>('');
  protected readonly draftError = signal<string | null>(null);

  protected readonly formattedRange = computed(() => {
    const from = this.fromDate();
    const to = this.toDate();
    return from && to ? `${formatLocalDate(from)} – ${formatLocalDate(to)}` : '';
  });

  // Capacity 1 admits no quantity but 1 (decision `0005`'s amendment) — the
  // stepper has nothing to offer on an exclusive resource, so it's absent
  // entirely rather than shown disabled.
  protected readonly showQuantityStepper = computed(() => (this.resource()?.capacity ?? 1) > 1);

  protected readonly availabilityResponse = signal<AvailabilityResponse | null>(null);
  protected readonly availabilityLoading = signal(false);
  protected readonly availabilityError = signal(false);

  // Built from the response's own echoed fromLocalDate/toLocalDate and
  // timeZoneId, not the live fromDate()/toDate()/resource() signals —
  // self-consistent with whatever was actually asked and answered, rather
  // than coupled to the exact instant those signals happen to read at.
  protected readonly dayRows = computed(() => {
    const response = this.availabilityResponse();
    if (!response) {
      return [];
    }
    return buildDayRows(datesInRange(response.fromLocalDate, response.toLocalDate), response.intervals, response.timeZoneId);
  });

  protected readonly axis = computed(() => computeAxis(this.dayRows()));
  protected readonly axisTicks = computed(() => buildAxisTicks(this.axis()));

  // isArchived gets its own state, distinct from "every day in range is
  // closed" (decision `0020`) — an empty dayRows() can't tell those apart on
  // its own.
  protected readonly isArchivedResource = computed(() => this.availabilityResponse()?.isArchived ?? false);

  // Step 6: selecting a bar. selectedSegment is the *originally clicked* bar
  // — its own startMinutes/endMinutes/startUtc/endUtc are the outer bounds a
  // viewer can narrow via the Start/End dropdowns, never widen past. The two
  // minute signals below are what those dropdowns actually edit, initialized
  // to the full segment on click (matching the design: clicking a bar
  // selects it whole, refinement is opt-in).
  //
  // Compared by reference (`selectedSegment() === segment` in the template),
  // safe because dayRows() is a computed(): the same segment object survives
  // re-renders until availabilityResponse() itself changes, at which point
  // clearSelection() below has already run and there's nothing stale to
  // compare against.
  protected readonly selectedSegment = signal<DaySegment | null>(null);
  protected readonly selectedStartMinutes = signal(0);
  protected readonly selectedEndMinutes = signal(0);

  // Bounded by the resource's own minDurationMinutes, not just one step: the
  // latest legal Start is however far back from the segment's end the
  // minimum booking length requires, so every rendered Start option still
  // leaves room for at least one legal End.
  protected readonly startTimeOptions = computed(() => {
    const segment = this.selectedSegment();
    const resource = this.resource();
    if (!segment || !resource) {
      return [];
    }
    const latestStart = Math.max(segment.startMinutes, segment.endMinutes - effectiveMinDuration(resource));
    return buildTimeOptions(segment.startMinutes, latestStart);
  });

  // Bounded on both sides by the resource's own duration limits relative to
  // whatever Start is currently selected — the min pushes the earliest legal
  // End forward, the max pulls the latest legal End back, both still clamped
  // inside the segment itself.
  protected readonly endTimeOptions = computed(() => {
    const segment = this.selectedSegment();
    const resource = this.resource();
    if (!segment || !resource) {
      return [];
    }
    const start = this.selectedStartMinutes();
    const earliestEnd = Math.min(start + effectiveMinDuration(resource), segment.endMinutes);
    const latestEnd = Math.min(start + effectiveMaxDuration(resource), segment.endMinutes);
    return buildTimeOptions(earliestEnd, Math.max(earliestEnd, latestEnd));
  });

  // A defensive fallback, not the primary guard — startTimeOptions/
  // endTimeOptions already keep the dropdowns from *offering* an invalid
  // combination. This only fires when the clicked segment itself is shorter
  // than the resource's own minDurationMinutes (a blackout can cut a window
  // down to an odd, short remainder), the one case neither dropdown's own
  // bounds can route around.
  //
  // Item 5: checks resource.minDurationMinutes directly, not
  // effectiveMinDuration — a resource with no configured minimum (`null`)
  // is valid at whole-second precision on the backend, so it must never see
  // a "requires at least 15 minutes" message that attributes the UI's own
  // dropdown granularity to it as if it were the resource's own policy.
  protected readonly durationError = computed(() => {
    const resource = this.resource();
    const segment = this.selectedSegment();
    if (!resource || !segment) {
      return null;
    }
    const duration = this.selectedEndMinutes() - this.selectedStartMinutes();
    const minDuration = resource.minDurationMinutes;
    if (minDuration !== null && duration < minDuration) {
      return `This resource requires bookings of at least ${formatSelectionDuration(minDuration)}, but this slot only fits ${formatSelectionDuration(segment.endMinutes - segment.startMinutes)}.`;
    }
    const maxDuration = effectiveMaxDuration(resource);
    if (duration > maxDuration) {
      return `This resource allows bookings of at most ${formatSelectionDuration(maxDuration)}.`;
    }
    return null;
  });

  protected readonly selectedDateLabel = computed(() => {
    const segment = this.selectedSegment();
    return segment ? formatLocalDateWithWeekdayAndYear(segment.date) : '';
  });

  protected readonly selectedTimeRangeLabel = computed(
    () => `${formatMinutesOfDay(this.selectedStartMinutes())}–${formatMinutesOfDay(this.selectedEndMinutes())}`,
  );

  protected readonly selectedDurationLabel = computed(() =>
    formatSelectionDuration(this.selectedEndMinutes() - this.selectedStartMinutes()),
  );

  private resourceId: string;

  // Fed by retry() alongside route-id changes, so both funnel through the
  // same switchMap below rather than each racing it with a separate call to
  // a shared load() method.
  private readonly retry$ = new Subject<void>();

  constructor() {
    const initialId = this.route.snapshot.paramMap.get('id');
    if (!initialId) {
      throw new Error('AvailabilityComponent requires an "id" route param.');
    }
    this.resourceId = initialId;

    // switchMap (item 4) is what makes a stale response impossible rather
    // than merely ignored: a route-id change or a retry() both cancel
    // whatever resource fetch is still in flight (switchMap unsubscribes
    // the previous inner Observable, which aborts the underlying HTTP
    // request) before starting the next one. The availability fetch this
    // cascades into (applyResourceResult -> fetchAvailability) is guarded
    // separately, by fetchAvailability's own latestAvailabilityRequestId —
    // a different race (quantity/range controls, not route navigation)
    // that already had its own tests before this change.
    const idChanges$ = this.route.paramMap.pipe(
      map((params) => params.get('id')),
      filter((id): id is string => !!id),
      distinctUntilChanged(),
    );

    merge(idChanges$, this.retry$.pipe(map(() => this.resourceId)))
      .pipe(
        map((id) => {
          const isNewResource = id !== this.resourceId;
          this.resourceId = id;
          if (isNewResource) {
            // A different resource can have a different timezone and a
            // different capacity — the previous range/quantity aren't
            // necessarily meaningful for it, so both reset rather than
            // carrying over silently.
            this.fromDate.set('');
            this.toDate.set('');
            this.quantity.set(1);
            this.availabilityResponse.set(null);
            this.availabilityError.set(false);
          }
          return id;
        }),
        switchMap((id) => this.fetchResource$(id)),
        takeUntilDestroyed(),
      )
      .subscribe((result) => this.applyResourceResult(result));

    // This screen's own crumb is inserted, not overridden (see
    // BreadcrumbService's comment on why availability needs the second
    // slot) — cleared on destroy so it can't leak onto the next page, the
    // same discipline ResourceDetailComponent already applies to its own
    // override.
    this.destroyRef.onDestroy(() => this.breadcrumbService.setInsertBeforeLast(null));
  }

  protected retry(): void {
    this.retry$.next();
  }

  protected typeLabel(type: ResourceDetail['resourceType']): string {
    return resourceTypeLabel(type);
  }

  protected capacityLabel(resource: ResourceDetail): string {
    return resourceCapacityLabel(resource);
  }

  protected approvalLabel(resource: ResourceDetail): string {
    return resource.requiresApproval ? 'Approval required' : 'No approval required';
  }

  protected goToToday(): void {
    const resource = this.resource();
    if (!resource) {
      return;
    }
    const today = resourceLocalToday(resource.timeZoneId);
    this.fromDate.set(today);
    this.toDate.set(addDays(today, DEFAULT_WINDOW_DAYS - 1));
    this.fetchAvailability();
  }

  // Always resets to a plain DEFAULT_WINDOW_DAYS-wide window from the new
  // start, even if the current range was a custom one picked via the
  // popover — the arrows are this screen's "shift by a week" affordance
  // (matching the design's own prev/next controls), not a generic
  // "translate whatever range is active" operation.
  protected shiftWeek(direction: 1 | -1): void {
    const newFrom = addDays(this.fromDate(), DEFAULT_WINDOW_DAYS * direction);
    this.fromDate.set(newFrom);
    this.toDate.set(addDays(newFrom, DEFAULT_WINDOW_DAYS - 1));
    this.fetchAvailability();
  }

  // Re-clicking the range display while the popover is already open closes
  // it (discarding any in-progress draft edit, same as Cancel) rather than
  // re-opening it on top of itself — the toggle is what makes the button
  // double as its own close affordance, per the owner's own correction: a
  // picker that can only be dismissed via the Cancel button is bad UX.
  protected toggleRangePicker(): void {
    if (this.showRangePicker()) {
      this.cancelRangePicker();
      return;
    }
    this.draftFrom.set(this.fromDate());
    this.draftTo.set(this.toDate());
    this.draftError.set(null);
    this.showRangePicker.set(true);
  }

  protected cancelRangePicker(): void {
    this.showRangePicker.set(false);
  }

  // Two independent "click elsewhere dismisses it" behaviors share one
  // document-level listener rather than two, since both need the same
  // event.target. `.closest()` against each own wrapper class, not a
  // ViewChild, because neither wrapper exists in the DOM until its own
  // conditional branch renders — a ViewChild would have to guard the same
  // case anyway, so there's nothing simpler to gain by injecting ElementRef.
  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;

    // The range popover, dismissed the same way Cancel does.
    if (this.showRangePicker() && !target?.closest('.range-picker-wrapper')) {
      this.cancelRangePicker();
    }

    // The current selection (owner's request): a click anywhere that isn't
    // a bookable bar (switching the selection is its own click, handled by
    // selectSegment) or the selection UI itself (the overlay/handles or the
    // summary panel's own dropdowns/buttons) clears it, the same as the
    // explicit "Clear selection" link.
    if (this.selectedSegment() && !target?.closest('.segment, .selection-overlay, .selection-panel')) {
      this.clearSelection();
    }
  }

  protected onDraftFromChange(event: Event): void {
    this.draftFrom.set((event.target as HTMLInputElement).value);
  }

  protected onDraftToChange(event: Event): void {
    this.draftTo.set((event.target as HTMLInputElement).value);
  }

  protected applyRangePicker(): void {
    const error = validateRange(this.draftFrom(), this.draftTo());
    if (error) {
      this.draftError.set(error);
      return;
    }
    this.fromDate.set(this.draftFrom());
    this.toDate.set(this.draftTo());
    this.showRangePicker.set(false);
    this.fetchAvailability();
  }

  protected incrementQuantity(): void {
    const capacity = this.resource()?.capacity ?? 1;
    this.quantity.update((q) => Math.min(q + 1, capacity));
    this.fetchAvailability();
  }

  protected decrementQuantity(): void {
    this.quantity.update((q) => Math.max(q - 1, 1));
    this.fetchAvailability();
  }

  protected retryAvailability(): void {
    this.fetchAvailability();
  }

  protected dayLabel(date: LocalDateString): string {
    return formatLocalDateWithWeekday(date);
  }

  // Item 6: the availability API returns bookable intervals only, so an
  // empty day doesn't tell fully booked, blacked out, or insufficient
  // pooled capacity apart from a genuinely closed weekday — the API
  // deliberately doesn't expose which one it is (CLAUDE.md §6's own
  // availability-window note: narrowing a window cancels nothing, so
  // "inside a window" isn't an invariant kept after creation, and neither
  // is the reverse). "No bookable hours" is only ever shown when the
  // already-loaded ResourceDetail's own availabilityWindows *prove* that
  // weekday has no window at all; every other empty day gets the honest,
  // non-specific "No availability".
  protected emptyDayLabel(date: LocalDateString): string {
    return this.isWeekdayProvenClosed(date) ? 'No bookable hours' : 'No availability';
  }

  private isWeekdayProvenClosed(date: LocalDateString): boolean {
    const windows = this.resource()?.availabilityWindows;
    if (!windows) {
      return false;
    }
    const weekday = weekdayOf(date);
    return !windows.some((w) => w.weekday === weekday);
  }

  // "Tuesday, Sep 22, 10:00 to 12:00, 3 units remaining" (item 10) — the
  // segment button's own visible text is deliberately short ("3 left", or
  // just the time range for an exclusive resource), so this is what a
  // screen reader announces instead: the day and full time range it might
  // otherwise have to infer from the row/axis alone, spelled out with "to"
  // rather than the visible label's en dash (which a screen reader would
  // read as "dash", not "to").
  protected segmentAccessibleLabel(date: LocalDateString, segment: DaySegment): string {
    const dayPart = formatLocalDateWithFullWeekday(date);
    const timePart = `${formatMinutesOfDay(segment.startMinutes)} to ${formatMinutesOfDay(segment.endMinutes)}`;
    const resource = this.resource();
    if (resource && resource.capacity > 1) {
      const unitWord = segment.remainingCapacity === 1 ? 'unit' : 'units';
      return `${dayPart}, ${timePart}, ${segment.remainingCapacity} ${unitWord} remaining`;
    }
    return `${dayPart}, ${timePart}`;
  }

  protected formatTick(minutes: number): string {
    return formatMinutesOfDay(minutes);
  }

  // Positioned the same way a segment is (a true percentage along the axis),
  // not flex-distributed — the axis start doesn't always land exactly on a
  // 2-hour boundary (e.g. an axis of 07:00-11:00 has ticks at 08:00/10:00,
  // which are 25%/75% along it, not 0%/100%), so evenly spacing the tick
  // *elements* would draw them in the wrong place relative to the segments
  // underneath them.
  protected tickLeftPercent(minutes: number): number {
    const axis = this.axis();
    const span = axis.endMinutes - axis.startMinutes;
    return ((minutes - axis.startMinutes) / span) * 100;
  }

  protected segmentLeftPercent(segment: DaySegment): number {
    return minuteSpanStylePercent(segment, this.axis()).leftPercent;
  }

  protected segmentWidthPercent(segment: DaySegment): number {
    return minuteSpanStylePercent(segment, this.axis()).widthPercent;
  }

  // The burgundy overlay (owner's correction: the *whole* clicked bar
  // turning burgundy didn't show which part of it was actually selected)
  // sits on top of the original bar, positioned by the live
  // selectedStartMinutes/selectedEndMinutes rather than the segment's own
  // full bounds — it shrinks and slides as the viewer narrows the selection
  // via the dropdowns or the drag handles.
  protected selectionOverlayLeftPercent(): number {
    return minuteSpanStylePercent(
      { startMinutes: this.selectedStartMinutes(), endMinutes: this.selectedEndMinutes() },
      this.axis(),
    ).leftPercent;
  }

  protected selectionOverlayWidthPercent(): number {
    return minuteSpanStylePercent(
      { startMinutes: this.selectedStartMinutes(), endMinutes: this.selectedEndMinutes() },
      this.axis(),
    ).widthPercent;
  }

  // Exclusive resource -> the actual time range (a single instance either
  // way, so the span itself is the useful information); pooled resource ->
  // how many units are still free, matching the design's "5 left" / "3
  // left" bars — the same axis (resource.capacity > 1) showQuantityStepper
  // already uses to decide exclusive vs. pooled.
  protected segmentLabel(segment: DaySegment): string {
    const resource = this.resource();
    if (resource && resource.capacity > 1) {
      return `${segment.remainingCapacity} left`;
    }
    return `${formatMinutesOfDay(segment.startMinutes)} – ${formatMinutesOfDay(segment.endMinutes)}`;
  }

  protected isSegmentSelected(segment: DaySegment): boolean {
    return this.selectedSegment() === segment;
  }

  // Defaults to the *longest legal booking starting at the beginning of the
  // slot* — segment start through the resource's own maxDurationMinutes —
  // rather than the whole segment (owner's correction). Selecting the whole
  // segment by default meant a resource with, say, a 4-hour max and a
  // 9-hour-open slot landed on an immediately-invalid selection every time;
  // this is never invalid on click unless the segment itself is shorter
  // than the resource's minimum (durationError's own remaining case). With
  // no maxDurationMinutes set, effectiveMaxDuration is Infinity and this
  // reduces to the old "whole segment" behavior exactly.
  protected selectSegment(segment: DaySegment): void {
    this.selectedSegment.set(segment);
    const resource = this.resource();
    const maxDuration = resource ? effectiveMaxDuration(resource) : Number.POSITIVE_INFINITY;
    this.selectedStartMinutes.set(segment.startMinutes);
    this.selectedEndMinutes.set(Math.min(segment.startMinutes + maxDuration, segment.endMinutes));
  }

  protected clearSelection(): void {
    this.selectedSegment.set(null);
    this.selectedStartMinutes.set(0);
    this.selectedEndMinutes.set(0);
  }

  protected onSelectedStartChange(event: Event): void {
    const minutes = Number((event.target as HTMLSelectElement).value);
    this.selectedStartMinutes.set(minutes);
    this.keepEndValidAfterStartChange(minutes);
  }

  // Keeps End at least the resource's own minDurationMinutes ahead of
  // whatever Start just became — the dropdowns are populated fresh each time
  // (endTimeOptions depends on selectedStartMinutes), but the signal's own
  // value has to move too, or it could point at an option that no longer
  // exists (or one that's technically still "after Start" but shorter than
  // the resource allows).
  private keepEndValidAfterStartChange(newStart: number): void {
    const resource = this.resource();
    const segment = this.selectedSegment();
    const minDuration = resource ? effectiveMinDuration(resource) : TIME_OPTION_STEP_MINUTES;
    if (this.selectedEndMinutes() - newStart < minDuration) {
      const ceiling = segment?.endMinutes ?? newStart + minDuration;
      this.selectedEndMinutes.set(Math.min(newStart + minDuration, ceiling));
    }
  }

  protected onSelectedEndChange(event: Event): void {
    this.selectedEndMinutes.set(Number((event.target as HTMLSelectElement).value));
  }

  // Converts the (possibly narrowed) local-minutes selection back to real
  // UTC instants — DST-safe (item 7): resourceLocalMinutesToUtc inverts the
  // resource's own IANA timezone rather than assuming local and UTC minutes
  // move in lockstep, which a flat offset from the segment's own startUtc
  // does not across a transition. See that function's own comment for the
  // two edge cases (a gap, an ambiguous repeated hour) it can't answer
  // exactly, and why that's accepted.
  protected continueToBooking(resource: ResourceDetail): void {
    const segment = this.selectedSegment();
    // The button is disabled whenever durationError() is set — this is a
    // second guard for anything that could still reach the method
    // programmatically, not the primary defense.
    if (!segment || this.durationError()) {
      return;
    }
    const startUtc = resourceLocalMinutesToUtc(segment, this.selectedStartMinutes(), resource.timeZoneId);
    const endUtc = resourceLocalMinutesToUtc(segment, this.selectedEndMinutes(), resource.timeZoneId);

    // Phase 3 builds the real booking form at this route; for now it's the
    // same placeholder every other not-yet-built screen loads — the router
    // state is what Phase 3 reads to pre-fill the form, carried forward now
    // so nothing has to be re-derived once that screen exists (mirrors
    // Phase 1's own precedent of wiring a route to a placeholder ahead of
    // the phase that gives it a real destination).
    void this.router.navigate(['/resources', resource.id, 'book'], {
      state: { startUtc, endUtc, quantity: this.quantity() },
    });
  }

  // Dragging the overlay (owner's request, step 6 follow-up: edges resize
  // the selection, the body slides the whole window). Uses Pointer Capture
  // rather than document-level mousemove/mouseup listeners: setPointerCapture
  // keeps every subsequent pointermove/pointerup routed to the captured
  // element even once the pointer strays outside it, which is exactly the
  // "drag anywhere, release anywhere" behavior this needs, for mouse, touch
  // and pen alike, with no manual listener teardown to get wrong.
  private dragEdge: 'start' | 'end' | 'move' | null = null;
  private dragTrackRect: DOMRect | null = null;
  // 'move' drag only: the pointer's own starting position and the
  // selection's own start/end at that moment, so the whole window can be
  // translated by exactly how far the pointer has moved rather than jumping
  // to track the pointer's absolute position (which is what the edge
  // handles do, and is the wrong feel for dragging the body).
  private dragOriginClientX = 0;
  private dragOriginStart = 0;
  private dragOriginEnd = 0;

  protected onHandlePointerDown(edge: 'start' | 'end', event: PointerEvent): void {
    const handle = event.currentTarget as HTMLElement;
    const track = handle.closest('.day-track');
    if (!(track instanceof HTMLElement)) {
      return;
    }
    // Without this, the same pointerdown bubbles from the handle up to the
    // overlay's own listener (native DOM bubbling — Angular's (pointerdown)
    // binding doesn't suppress it), which would immediately overwrite
    // dragEdge with 'move' and break edge-dragging entirely.
    event.stopPropagation();
    handle.setPointerCapture(event.pointerId);
    this.dragEdge = edge;
    // Cached once at drag start rather than re-read on every pointermove —
    // the track's own size and position don't change mid-drag, so there's
    // nothing to gain from repeated layout reads.
    this.dragTrackRect = track.getBoundingClientRect();
    event.preventDefault();
  }

  protected onOverlayPointerDown(event: PointerEvent): void {
    const overlay = event.currentTarget as HTMLElement;
    const track = overlay.closest('.day-track');
    if (!(track instanceof HTMLElement)) {
      return;
    }
    overlay.setPointerCapture(event.pointerId);
    this.dragEdge = 'move';
    this.dragTrackRect = track.getBoundingClientRect();
    this.dragOriginClientX = event.clientX;
    this.dragOriginStart = this.selectedStartMinutes();
    this.dragOriginEnd = this.selectedEndMinutes();
    event.preventDefault();
  }

  protected onOverlayPointerMove(event: PointerEvent): void {
    if (this.dragEdge !== 'move' || !this.dragTrackRect) {
      return;
    }
    const segment = this.selectedSegment();
    if (!segment) {
      return;
    }

    const axis = this.axis();
    const minutesPerPixel = (axis.endMinutes - axis.startMinutes) / this.dragTrackRect.width;
    const rawDeltaMinutes = (event.clientX - this.dragOriginClientX) * minutesPerPixel;
    const snappedDelta = Math.round(rawDeltaMinutes / TIME_OPTION_STEP_MINUTES) * TIME_OPTION_STEP_MINUTES;

    // Duration is fixed for a move-drag (only the window's position
    // changes) — clamped so neither edge can leave the segment's own
    // bounds, which automatically keeps the other edge in bounds too since
    // the gap between them never changes.
    const duration = this.dragOriginEnd - this.dragOriginStart;
    const lowerBound = segment.startMinutes;
    const upperBound = Math.max(lowerBound, segment.endMinutes - duration);
    const newStart = clamp(this.dragOriginStart + snappedDelta, lowerBound, upperBound);

    this.selectedStartMinutes.set(newStart);
    this.selectedEndMinutes.set(newStart + duration);
  }

  protected onHandlePointerMove(edge: 'start' | 'end', event: PointerEvent): void {
    if (this.dragEdge !== edge || !this.dragTrackRect) {
      return;
    }
    const resource = this.resource();
    const segment = this.selectedSegment();
    if (!resource || !segment) {
      return;
    }

    const axis = this.axis();
    const fraction = clamp((event.clientX - this.dragTrackRect.left) / this.dragTrackRect.width, 0, 1);
    const rawMinutes = axis.startMinutes + fraction * (axis.endMinutes - axis.startMinutes);
    const snapped = Math.round(rawMinutes / TIME_OPTION_STEP_MINUTES) * TIME_OPTION_STEP_MINUTES;

    const minDuration = effectiveMinDuration(resource);
    const maxDuration = effectiveMaxDuration(resource);

    // Same bounds the dropdowns already enforce (segment extent + resource
    // duration limits relative to the *other* edge) — dragging can't reach
    // an invalid selection any more than picking from the select could.
    if (edge === 'start') {
      const lowerBound = Math.max(segment.startMinutes, this.selectedEndMinutes() - maxDuration);
      const upperBound = Math.min(segment.endMinutes, this.selectedEndMinutes() - minDuration);
      this.selectedStartMinutes.set(clamp(snapped, lowerBound, Math.max(lowerBound, upperBound)));
    } else {
      const lowerBound = Math.max(segment.startMinutes, this.selectedStartMinutes() + minDuration);
      const upperBound = Math.min(segment.endMinutes, this.selectedStartMinutes() + maxDuration);
      this.selectedEndMinutes.set(clamp(snapped, Math.min(lowerBound, upperBound), upperBound));
    }
  }

  protected onHandlePointerUp(): void {
    this.dragEdge = null;
    this.dragTrackRect = null;
  }

  // Only when unset — a route-param change already clears both signals
  // itself (see the paramMap subscription), so this only ever fires on a
  // genuinely fresh load, never re-arming after the viewer picks their own
  // range.
  private initializeRangeIfUnset(resource: ResourceDetail): void {
    if (this.fromDate()) {
      return;
    }
    const today = resourceLocalToday(resource.timeZoneId);
    this.fromDate.set(today);
    this.toDate.set(addDays(today, DEFAULT_WINDOW_DAYS - 1));
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

  private applyResourceResult(result: ResourceLoadResult): void {
    this.loading.set(false);
    if (result.kind === 'error') {
      if (result.error instanceof HttpErrorResponse && result.error.status === 404) {
        this.notFound.set(true);
      } else {
        this.loadError.set(true);
      }
      return;
    }

    const resource = result.resource;
    this.resource.set(resource);
    this.breadcrumbService.setInsertBeforeLast(resource.name);
    // Clamp before initializing/fetching, so the very first availability
    // request already carries the corrected quantity rather than one
    // fetchAvailability() call with a stale value followed by a second,
    // redundant one.
    if (this.quantity() > resource.capacity) {
      this.quantity.set(1);
    }
    this.initializeRangeIfUnset(resource);
    this.fetchAvailability();
  }

  // Guards against a stale response overwriting a newer one — the same
  // "which request is still current" idea ResourceListComponent's own
  // latestRequestId already established, needed here because the range
  // picker's arrows/Today/quantity stepper can all fire a new request before
  // a previous one has come back.
  private latestAvailabilityRequestId = 0;

  // Not piped through takeUntilDestroyed: a response arriving after the
  // component is gone just writes to an orphaned signal, which is harmless
  // and momentary (this method has no interval/long-lived subscription to
  // leak) — unlike the paramMap subscription above, which genuinely lives
  // for the component's whole lifetime and needs the explicit teardown.
  private fetchAvailability(): void {
    if (!this.fromDate() || !this.toDate()) {
      return;
    }
    // A new response rebuilds dayRows() from scratch, so any previously
    // selected segment's object reference is about to go stale — clearing
    // here (rather than leaving a selection pointing at a bar that no
    // longer exists) is what isSegmentSelected()'s reference comparison
    // depends on staying correct.
    this.clearSelection();
    const requestId = ++this.latestAvailabilityRequestId;
    this.availabilityLoading.set(true);
    this.availabilityError.set(false);

    this.availabilityService
      .get(this.resourceId, { from: this.fromDate(), to: this.toDate(), quantity: this.quantity() })
      .subscribe({
        next: (response) => {
          if (requestId !== this.latestAvailabilityRequestId) {
            return;
          }
          this.availabilityResponse.set(response);
          this.availabilityLoading.set(false);
        },
        error: () => {
          if (requestId !== this.latestAvailabilityRequestId) {
            return;
          }
          this.availabilityLoading.set(false);
          this.availabilityError.set(true);
        },
      });
  }
}
