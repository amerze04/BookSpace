import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, distinctUntilChanged, filter, map, merge, of, switchMap } from 'rxjs';
import { BreadcrumbService } from '../../layout/breadcrumb.service';
import { ResourcesService } from '../resources/resources.service';
import { ResourceDetail, ResourceType } from '../resources/resources.models';
import { ResourceTypeIconComponent } from '../../shared/resource-type/resource-type-icon.component';
import { resourceCapacityLabel, resourceTypeLabel } from '../../shared/resource-type/resource-type';
import {
  formatDurationWords,
  formatLocalDateWithWeekdayAndYear,
  formatMinutesOfDay,
  utcToResourceLocal,
} from '../availability/local-date';
import {
  RECURRENCE_FREQUENCIES,
  RecurrenceFrequency,
  RecurrenceOccurrenceReport,
} from './recurrence.models';
import {
  RecurrenceEndConditionKind,
  RecurrenceFormValue,
  RecurrenceUnavailableReason,
  ResourceSchedule,
  buildRecurrenceRequest,
  canComputeImpliedEndDate,
  defaultRecurrenceFor,
  hasRecurrenceErrors,
  impliedEndDate,
  isValidLocalDate,
  recurrenceFromSelection,
  recurrenceUnavailableReason,
  validateRecurrenceForm,
} from './recurrence-form';
import { RECURRENCE_FIELD_NAMES, describeRecurrenceRejection } from './recurrence-rejection';
import {
  SeriesOutcome,
  occurrenceReasonLabel,
  parseSeriesRefusal,
  seriesOutcomeFromResponse,
  summarizeOccurrences,
  summaryLine,
} from './recurrence-outcome';
import { RecurrenceRulesService } from './recurrence-rules.service';
import { BookingMode, BookingSelection, parseBookingMode, parseBookingSelection } from './booking-arrival';
import { BookingFieldName, BookingRejection, describeBookingRejection } from './booking-rejection';
import { BookingsService } from './bookings.service';
import { CreateBookingResponse, MAX_BOOKING_TITLE_LENGTH } from './booking.models';

type ResourceLoadResult = { kind: 'success'; resource: ResourceDetail } | { kind: 'error'; error: unknown };

// The span the member is about to book, rendered in one timezone. Built twice
// per selection when the viewer's own zone differs from the resource's — see
// viewerZoneSpan.
interface SpanLabels {
  date: string;
  timeRange: string;
}

function spanLabels(span: { startUtc: string; endUtc: string }, timeZoneId: string): SpanLabels {
  const start = utcToResourceLocal(span.startUtc, timeZoneId);
  const end = utcToResourceLocal(span.endUtc, timeZoneId);

  // An overnight span lands on two calendar days, so the end carries its own
  // date rather than being read against the start's.
  const endLabel =
    end.date === start.date
      ? formatMinutesOfDay(end.minutesOfDay)
      : `${formatMinutesOfDay(end.minutesOfDay)} (${formatLocalDateWithWeekdayAndYear(end.date)})`;

  return {
    date: formatLocalDateWithWeekdayAndYear(start.date),
    timeRange: `${formatMinutesOfDay(start.minutesOfDay)} – ${endLabel}`,
  };
}

// One instant, read in one zone: "Fri, Sep 18, 2026, 11:03". Used for the
// approval expiry, which — unlike the booked span — is not a fact about the
// resource's schedule but a deadline people watch, so it reads in the
// viewer's own zone with that zone named.
function instantLabel(utcIso: string, timeZoneId: string): string {
  const instant = utcToResourceLocal(utcIso, timeZoneId);
  return `${formatLocalDateWithWeekdayAndYear(instant.date)}, ${formatMinutesOfDay(instant.minutesOfDay)}`;
}

function durationMinutesBetween(startUtc: string, endUtc: string): number {
  return Math.round((Date.parse(endUtc) - Date.parse(startUtc)) / 60_000);
}

function stringFrom(event: Event): string {
  return (event.target as HTMLInputElement | HTMLSelectElement).value;
}

// An empty or non-numeric input reads as NaN rather than 0, so the validator
// reports "must be a whole number of 1 or more" instead of silently treating a
// cleared box as a zero the server would then refuse.
function numberFrom(event: Event): number {
  const raw = stringFrom(event);
  return raw === '' ? NaN : Number(raw);
}

const FREQUENCY_UNITS: Record<RecurrenceFrequency, string> = {
  Daily: 'day',
  Weekly: 'week',
  Monthly: 'month',
};

// "Every week", "Every 2 weeks" — a plain function rather than a method,
// because the outcome panel has to describe the series that was *submitted*,
// not the one the form currently holds (see submittedRecurrence).
function patternLabel(value: RecurrenceFormValue): string {
  const unit = FREQUENCY_UNITS[value.frequency];
  return value.intervalValue === 1 ? `Every ${unit}` : `Every ${value.intervalValue} ${unit}s`;
}

// Why the recurring form cannot be submitted against this resource at all, in
// a member's words. Not an error against a control — no value they could type
// would fix any of these — so it replaces the fields rather than sitting under
// one.
//
// Deliberately distinct from "nothing is free": a resource with hours that is
// simply booked out still gets the form, submits, and receives FR-5.4's
// per-occurrence answer. These three say the resource has no bookable hours to
// aim a series at in the first place.
const RECURRENCE_UNAVAILABLE_COPY: Record<RecurrenceUnavailableReason, string> = {
  noOpeningHours:
    "This resource currently has no bookable hours configured, so a recurring series can't be "
    + 'set up for it yet. An administrator publishes a resource\'s opening hours.',
  durationLimitsConflict:
    "This resource's shortest allowed booking is longer than its longest, so no booking length "
    + 'would be accepted. An administrator can correct its duration limits.',
  noBookableWindow:
    "None of this resource's opening hours are long enough for the shortest booking it allows, "
    + 'so no occurrence could be booked.',
};

// Only ever seen before the resource has loaded — every real seeding goes
// through recurrence-form.ts's own defaults, which need the resource's
// schedule to pick a sensible day and time.
const EMPTY_RECURRENCE: RecurrenceFormValue = {
  frequency: 'Weekly',
  intervalValue: 1,
  localStartTime: '09:00',
  localEndTime: '10:00',
  startDate: '',
  endCondition: 'occurrenceCount',
  endDate: '',
  occurrenceCount: 4,
};

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
  private readonly bookingsService = inject(BookingsService);
  private readonly recurrenceRulesService = inject(RecurrenceRulesService);
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

  private readonly modeFromUrl = toSignal(
    this.route.queryParamMap.pipe(map((params) => parseBookingMode(params))),
    { initialValue: 'oneOff' as BookingMode },
  );

  // An archived resource cannot be booked at all (ResourceArchived), so it
  // short-circuits both the form and the arrival state — the same
  // "Archived — not bookable" treatment the list and detail screens already
  // give in place of their own booking CTAs, rather than a form that could
  // only ever be refused on submit.
  protected readonly isArchived = computed(() => this.resource()?.isArchived ?? false);

  // ---- Step 3: the one-off form ----

  protected readonly maxTitleLength = MAX_BOOKING_TITLE_LENGTH;

  protected readonly title = signal('');

  // A linkedSignal, not a plain signal seeded once: it is writable (the
  // stepper) but re-derives whenever its source changes, so arriving with a
  // different selection resets the stepper instead of carrying the previous
  // slot's number over. The nearest .NET analogue is a property with a
  // backing field that a recomputed dependency invalidates — except here the
  // framework does the invalidating.
  //
  // **Not clamped to capacity**, deliberately, and this was got wrong once:
  // clamping a hand-edited `?quantity=16` down to 1 made the booking succeed
  // for a quantity nobody asked for, and said nothing about it. This codebase
  // refuses rather than silently adjusts — decision `0015` rejects an
  // oversized `pageSize` instead of clamping it, for the same reason. What the
  // URL asked for is kept, and `capacityError` below refuses it with a message
  // and a way out.
  protected readonly quantity = linkedSignal(() => this.selection()?.quantity ?? 1);

  // Capacity 1 admits no quantity but 1 (decision `0005`'s amendment), so the
  // stepper is absent entirely rather than shown disabled — the same call
  // AvailabilityComponent's own stepper makes.
  protected readonly showQuantityStepper = computed(() => (this.resource()?.capacity ?? 1) > 1);

  // ---- Step 6: the recurring half ----
  //
  // One component, two form groups — not two routes (wp7-plan.md step 6). The
  // resource, the selected slot, the quantity and the title are shared by both
  // modes; only the *when* differs, which is exactly what the toggle switches.
  //
  // Seeded from `?mode=recurring`, the entry point that lets a member who
  // already knows their pattern come straight here: requiring a picked slot
  // first made the availability screen a toll booth for recurring bookings,
  // since a series names its own schedule and doesn't use the slot's instants
  // at all. Writable afterwards — the toggle is ordinary UI state — and
  // re-seeded if the URL itself changes.
  protected readonly mode = linkedSignal(() => this.modeFromUrl());

  // Pre-filled from the same selection the one-off half uses, read into the
  // resource's own local time (decision `0003`) — so switching to Recurring
  // keeps the time already picked instead of blanking the form. linkedSignal,
  // so arriving with a different slot re-seeds it rather than stranding the
  // previous one; every field stays editable afterwards, which is the whole
  // difference between the two halves (`POST /recurrence-rules` takes local
  // wall clock, so a hand-entered time needs no local->UTC inversion here —
  // the handler does it per occurrence, CLAUDE.md §4.3).
  protected readonly recurrence = linkedSignal<RecurrenceFormValue>(() => {
    const resource = this.resource();
    if (!resource) {
      return EMPTY_RECURRENCE;
    }

    const selection = this.selection();
    if (!selection) {
      // Arrived without a slot: seed from the resource's own schedule *and its
      // own clock*, so the form opens on the first opening span from now on
      // that can actually hold a booking — not on this morning's opening time
      // when it is already the afternoon there.
      //
      // Null when the resource's configuration offers no such span at all;
      // `recurrenceUnavailable` below is what the screen shows for that, and
      // this falls back to the blank rather than manufacturing something that
      // fails its own guards.
      const now = utcToResourceLocal(new Date().toISOString(), resource.timeZoneId);
      return defaultRecurrenceFor(this.schedule(), now) ?? EMPTY_RECURRENCE;
    }

    const start = utcToResourceLocal(selection.startUtc, resource.timeZoneId);
    const end = utcToResourceLocal(selection.endUtc, resource.timeZoneId);
    return recurrenceFromSelection(start.date, start.minutesOfDay, end.minutesOfDay);
  });

  // The resource facts both recurrence guards need, in the shape
  // recurrence-form.ts asks for. All of it comes off the detail read this
  // screen already performed — the window check costs no request.
  private readonly schedule = computed<ResourceSchedule>(() => ({
    minDurationMinutes: this.resource()?.minDurationMinutes ?? null,
    maxDurationMinutes: this.resource()?.maxDurationMinutes ?? null,
    availabilityWindows: this.resource()?.availabilityWindows ?? [],
  }));

  // Why a series cannot be set up against this resource at all — no opening
  // hours published, or none long enough for the shortest booking it allows.
  // A configuration fact, so it disables the submit and replaces the fields
  // rather than being reported against one of them.
  protected readonly recurrenceUnavailable = computed<RecurrenceUnavailableReason | null>(() =>
    this.resource() ? recurrenceUnavailableReason(this.schedule()) : null,
  );

  protected readonly recurrenceUnavailableMessage = computed<string | null>(() => {
    const reason = this.recurrenceUnavailable();
    return reason ? RECURRENCE_UNAVAILABLE_COPY[reason] : null;
  });

  protected readonly recurrenceErrors = computed(() =>
    validateRecurrenceForm(this.recurrence(), this.schedule()),
  );

  // A server-reported message wins over the client's own check for the same
  // control, exactly as `titleError` and `quantityError` already do: if the
  // backend refused the value, that is the more authoritative answer.
  private recurrenceFieldError(field: BookingFieldName, fromForm?: string): string | null {
    return this.serverFieldMessage(field) ?? fromForm ?? null;
  }

  protected readonly recurrenceStartDateError = computed(() =>
    this.recurrenceFieldError('startDate', this.recurrenceErrors().startDate),
  );
  protected readonly recurrenceIntervalError = computed(() =>
    this.recurrenceFieldError('interval', this.recurrenceErrors().interval),
  );
  protected readonly recurrenceTimesError = computed(() =>
    this.recurrenceFieldError('times', this.recurrenceErrors().times),
  );
  // The recurring half renders its own duration message: the one-off path's
  // `durationError` lives inside the panel that is hidden in recurring mode,
  // so a server `BookingDurationOutOfRange` for a series had nowhere to appear
  // at all before this pass.
  protected readonly recurrenceDurationError = computed(() =>
    this.recurrenceFieldError('duration', this.recurrenceErrors().duration),
  );
  protected readonly recurrenceOccurrenceCountError = computed(() =>
    this.recurrenceFieldError('occurrenceCount', this.recurrenceErrors().occurrenceCount),
  );
  protected readonly recurrenceEndDateError = computed(() =>
    this.recurrenceFieldError('endDate', this.recurrenceErrors().endDate),
  );

  // What the series actually implies, shown rather than left for the member to
  // work out — the same arithmetic the span guard uses, so the date on screen
  // is the date the rule really ends on.
  //
  // **Every input is checked before the arithmetic runs**, for the reason
  // `impliedEndDate`'s own header gives: it is `Date` arithmetic underneath
  // and throws on a cleared date box or an out-of-range count rather than
  // returning something odd — from a `computed` the template reads on every
  // keystroke.
  protected readonly recurrenceEndsLabel = computed(() => {
    const value = this.recurrence();
    if (value.endCondition === 'endDate') {
      return value.endDate && isValidLocalDate(value.endDate)
        ? `On ${formatLocalDateWithWeekdayAndYear(value.endDate)}`
        : '—';
    }
    if (
      !canComputeImpliedEndDate(value.frequency, value.intervalValue, value.startDate, value.occurrenceCount)
    ) {
      return '—';
    }
    const last = impliedEndDate(value.frequency, value.intervalValue, value.startDate, value.occurrenceCount);
    return `After ${value.occurrenceCount} ${value.occurrenceCount === 1 ? 'occurrence' : 'occurrences'} (last on ${formatLocalDateWithWeekdayAndYear(last)})`;
  });

  // "Every week", "Every 2 weeks" — the interval reads naturally rather than
  // as a raw number beside a frequency name.
  protected readonly recurrencePatternLabel = computed(() => patternLabel(this.recurrence()));

  protected readonly submitting = signal(false);

  protected readonly created = signal<CreateBookingResponse | null>(null);

  // Step 7: what came of a recurring submit. Set from a 201 *and* from the
  // all-refused 422, which carry the identical per-occurrence breakdown — see
  // recurrence-outcome.ts.
  protected readonly seriesOutcome = signal<SeriesOutcome | null>(null);

  // The series as it was **submitted**, kept so the outcome panel can describe
  // what was actually sent rather than what the form happens to hold now.
  //
  // Those are not the same thing: the form stays on screen for the length of
  // the request, and before this pass nothing stopped a member editing it
  // mid-flight — Weekly 09:00-10:00 goes out, Monthly 14:00-15:00 is on screen
  // when the 201 lands, and the confirmation described the series nobody
  // booked. The fields are disabled while submitting now (the first half of
  // the fix); this is the second, so the panel is faithful even if some future
  // path writes the form behind a submit.
  private readonly submittedRecurrence = signal<RecurrenceFormValue | null>(null);

  protected readonly submittedPatternLabel = computed(() => {
    const value = this.submittedRecurrence();
    return value ? patternLabel(value) : '';
  });

  protected readonly submittedTimesLabel = computed(() => {
    const value = this.submittedRecurrence();
    return value ? `${value.localStartTime}–${value.localEndTime}` : '';
  });

  protected readonly seriesSummary = computed(() => {
    const outcome = this.seriesOutcome();
    return outcome ? summarizeOccurrences(outcome.occurrences) : null;
  });

  // FR-7.1 again, one screen over from where the one-off panel says it: on an
  // approval-gated resource a created occurrence is `Pending`, so "3 booked"
  // would claim three held times that are in fact three requests.
  protected readonly seriesNeedsApproval = computed(() => this.resource()?.requiresApproval ?? false);

  protected readonly seriesSummaryLine = computed(() => {
    const summary = this.seriesSummary();
    return summary ? summaryLine(summary, this.seriesNeedsApproval()) : '';
  });

  protected readonly seriesOutcomeHeading = computed(() => {
    if (this.seriesCreatedNothing()) {
      return 'Nothing could be booked';
    }
    return this.seriesNeedsApproval() ? 'Series submitted' : 'Series booked';
  });

  // Nothing created is the 422's own shape (the handler compensates its
  // RecurrenceRule away), so this is what flips the panel from "series booked"
  // to "nothing could be booked" — one renderer, two framings.
  protected readonly seriesCreatedNothing = computed(
    () => this.seriesOutcome() !== null && this.seriesSummary()?.created === 0,
  );

  // Step 5: what the last submit was refused for, already resolved into
  // message *and* placement by `booking-rejection.ts` — this component never
  // branches on a reason code itself.
  protected readonly rejection = signal<BookingRejection | null>(null);

  protected readonly durationMinutes = computed(() => {
    const selection = this.selection();
    return selection ? durationMinutesBetween(selection.startUtc, selection.endUtc) : 0;
  });

  // A server-reported message wins over the client-side check for the same
  // control: if the backend refused the value, that is the more authoritative
  // answer — the same precedence `LoginComponent.fieldError` already applies.
  private serverFieldMessage(field: BookingFieldName): string | null {
    return this.rejection()?.fieldMessages[field] ?? null;
  }

  // Checked against the resource's *own* minDurationMinutes/maxDurationMinutes
  // and nothing else. `null` means "no rule configured", never a default — the
  // exact false-minimum bug the 2026-09-16 frontend hardening pass fixed in
  // the availability screen's `effectiveMinDuration`, which is worth not
  // reintroducing one screen later.
  //
  // A client-side check cannot be the authority (the handler raises
  // BookingDurationOutOfRange either way); it exists so a member is told
  // before spending a round trip, and so the request this form builds is one
  // the API could actually accept.
  protected readonly durationError = computed<string | null>(() => {
    const fromServer = this.serverFieldMessage('duration');
    if (fromServer) {
      return fromServer;
    }

    const resource = this.resource();
    const selection = this.selection();
    if (!resource || !selection) {
      return null;
    }

    const duration = this.durationMinutes();
    const min = resource.minDurationMinutes;
    if (min !== null && duration < min) {
      return `This resource requires bookings of at least ${formatDurationWords(min)}.`;
    }

    const max = resource.maxDurationMinutes;
    if (max !== null && duration > max) {
      return `This resource allows bookings of at most ${formatDurationWords(max)}.`;
    }

    return null;
  });

  // Bookings.Title is NVARCHAR(200) and the validator refuses more
  // (MaxTitleLength) — the input is bounded by maxlength as well, so this
  // catches a paste that slips past it rather than being the only guard.
  protected readonly titleError = computed<string | null>(() => {
    if (this.title().length > MAX_BOOKING_TITLE_LENGTH) {
      return `A title can be at most ${MAX_BOOKING_TITLE_LENGTH} characters.`;
    }
    return this.serverFieldMessage('title');
  });

  // A quantity the resource could never satisfy — which is reachable only
  // from the URL, since the stepper's own + button stops at capacity.
  // Checked here for the same reason the duration is: so the member is told
  // before spending a round trip, and so the request this form builds is one
  // the API could actually accept.
  //
  // Worth stating plainly because CLAUDE.md §6 currently reads otherwise: an
  // exclusive resource *can* answer `CapacityExceeded` — verified live, a
  // capacity-1 resource returns it for `quantity: 3` — because
  // `CreateBookingCommandRequestValidator` deliberately puts no upper bound on
  // quantity. So this is a real request the server refuses, not an impossible
  // one.
  protected readonly capacityError = computed<string | null>(() => {
    const resource = this.resource();
    const requested = this.quantity();
    if (!resource || requested <= resource.capacity) {
      return null;
    }

    return resource.capacity === 1
      ? `This resource is a single unit, but ${requested} were asked for. Only one can be booked at a time.`
      : `This resource has ${resource.capacity} units, but ${requested} were asked for.`;
  });

  protected readonly quantityError = computed<string | null>(
    () => this.serverFieldMessage('quantity') ?? this.capacityError(),
  );

  // Where a quantity problem is actually visible. With a stepper on screen it
  // belongs against that control; on an exclusive resource the stepper is
  // absent entirely (decision `0005`), so a field-level message would have
  // nothing to attach to and the member would see no explanation at all.
  protected readonly topOfFormMessage = computed<string | null>(() => {
    const fromRejection = this.rejection()?.formMessage;
    if (fromRejection) {
      return fromRejection;
    }
    return this.showQuantityStepper() ? null : this.quantityError();
  });

  // Going back to availability is the way out of a bad quantity: it re-picks a
  // slot and builds a fresh URL, which is the only control an exclusive
  // resource offers for it.
  protected readonly topOfFormRecheck = computed(
    () => this.rejection()?.recheckAvailability || (!this.showQuantityStepper() && this.quantityError() !== null),
  );

  // The span in the resource's own timezone — what "Thu, Sep 24, 09:15" means
  // for the room itself (decision `0003`), which is the reading the
  // availability screen offered and the one the member chose against.
  protected readonly resourceZoneSpan = computed<SpanLabels | null>(() => {
    const resource = this.resource();
    const selection = this.selection();
    return resource && selection ? spanLabels(selection, resource.timeZoneId) : null;
  });

  protected readonly viewerTimeZoneId = Intl.DateTimeFormat().resolvedOptions().timeZone;

  // The same instants in the *viewer's* browser zone, shown only when the two
  // differ — a member in Sarajevo booking a New York room needs to know when
  // to actually be there, and a booking screen that named only one of the two
  // zones would be quietly ambiguous. (`wp7-plan.md` §3's own display default:
  // a concrete instant renders in the viewer's local time; the resource's zone
  // is what the *availability* question was asked in.)
  protected readonly viewerZoneSpan = computed<SpanLabels | null>(() => {
    const resource = this.resource();
    const selection = this.selection();
    if (!resource || !selection || this.viewerTimeZoneId === resource.timeZoneId) {
      return null;
    }
    return spanLabels(selection, this.viewerTimeZoneId);
  });

  // Availability was answered for the quantity the member picked on the
  // previous screen; raising it here asks for something that response never
  // promised. Not blocked — the pool may well have room, and dbo.CreateBooking
  // is the only thing that can actually say — but said out loud, so a
  // CapacityExceeded rejection isn't a surprise.
  protected readonly quantityRaisedAboveChecked = computed(() => {
    const selection = this.selection();
    return selection !== null && this.quantity() > selection.quantity;
  });

  // ---- Step 4: the outcome ----
  //
  // Everything below reads the **created booking**, never the selection the
  // form was built from: what was reserved is whatever the server says was
  // reserved. The two agree today, but a confirmation panel that quietly
  // showed the request instead of the response would be the wrong one to
  // trust if they ever disagreed.

  // FR-7.1. The distinction this whole panel exists to make: a booking on an
  // approval-gated resource is created Pending, and the member has to be told
  // at the moment of booking rather than discovering it in a list later.
  protected readonly isPending = computed(() => this.created()?.status === 'Pending');

  // The create response names its instants startsAtUtc/endsAtUtc after the
  // columns, while a selection (and a bookable interval) uses startUtc/endUtc
  // — mapped here rather than teaching spanLabels both spellings.
  private createdSpanUtc(): { startUtc: string; endUtc: string } | null {
    const created = this.created();
    return created ? { startUtc: created.startsAtUtc, endUtc: created.endsAtUtc } : null;
  }

  protected readonly createdSpan = computed<SpanLabels | null>(() => {
    const resource = this.resource();
    const span = this.createdSpanUtc();
    return resource && span ? spanLabels(span, resource.timeZoneId) : null;
  });

  protected readonly createdViewerSpan = computed<SpanLabels | null>(() => {
    const resource = this.resource();
    const span = this.createdSpanUtc();
    if (!resource || !span || this.viewerTimeZoneId === resource.timeZoneId) {
      return null;
    }
    return spanLabels(span, this.viewerTimeZoneId);
  });

  protected readonly createdDurationLabel = computed(() => {
    const created = this.created();
    return created ? formatDurationWords(durationMinutesBetween(created.startsAtUtc, created.endsAtUtc)) : '';
  });

  // Who actually decides. A resource's assigned approvers (decision `0018`:
  // own-tenant, active, Approver/TenantAdmin) are listed by name — the detail
  // read already carries them, so nothing extra is fetched. With none
  // assigned, a TenantAdmin is still able to approve anything in the tenant,
  // which is what the empty case says rather than leaving the member with no
  // answer at all.
  protected readonly approverNames = computed(() => this.resource()?.approvers.map((a) => a.fullName) ?? []);

  // FR-7.4: a tenant that has set no ApprovalExpiryHours leaves requests
  // pending indefinitely — a legitimate configuration, not a missing value,
  // so null gets its own sentence rather than a blank date.
  protected readonly approvalExpiryLabel = computed<string | null>(() => {
    const expiresAtUtc = this.created()?.approval?.expiresAtUtc;
    return expiresAtUtc ? instantLabel(expiresAtUtc, this.viewerTimeZoneId) : null;
  });

  // Shared by both modes: a resource that can be booked at all, a title and a
  // quantity the API would accept, and nothing already in flight or done.
  // **Not** a selection — only the one-off half needs one.
  private readonly canSubmitCommon = computed(
    () =>
      !this.isArchived() &&
      !this.submitting() &&
      this.created() === null &&
      this.titleError() === null &&
      this.quantityError() === null,
  );

  protected readonly canSubmit = computed(() => {
    if (!this.canSubmitCommon()) {
      return false;
    }

    return this.mode() === 'oneOff'
      ? // The one-off half is pre-fill only: without a picked slot there are no
        // instants to send, and this client deliberately owns no local->UTC
        // inversion to invent them (CLAUDE.md §4.3).
        this.selection() !== null && this.durationError() === null
      : this.recurrenceIsValid() && this.seriesOutcome() === null;
  });

  protected readonly recurrenceIsValid = computed(
    () =>
      this.canSubmitCommon() &&
      // A resource with no bookable hours at all: no value in this form could
      // produce a single bookable occurrence, so there is nothing to submit.
      this.recurrenceUnavailable() === null &&
      !hasRecurrenceErrors(this.recurrenceErrors()) &&
      // Plus anything the server refused that the client's own guards would
      // not have. Cleared by editing the control it was reported against —
      // otherwise a server-only rule would lock the form permanently, since a
      // rejection is only cleared on the submit it is blocking.
      RECURRENCE_FIELD_NAMES.every((field) => this.serverFieldMessage(field) === null),
  );

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

  protected durationLabel(): string {
    return formatDurationWords(this.durationMinutes());
  }

  protected occurrenceLabel(occurrence: RecurrenceOccurrenceReport): string {
    return occurrenceReasonLabel(occurrence, this.seriesNeedsApproval());
  }

  // Back to the form after an all-refused series, with every field still as it
  // was — the panel tells the member to adjust the series, so there has to be
  // something to adjust it with. Only offered when nothing was created: a
  // series that exists is not something to edit here (that is Phase 4's
  // cancel, on My Bookings).
  protected editSeriesAgain(): void {
    this.seriesOutcome.set(null);
  }

  protected occurrenceDateLabel(occurrence: RecurrenceOccurrenceReport): string {
    return formatLocalDateWithWeekdayAndYear(occurrence.occurrenceDate);
  }

  // Safe *because* of the idempotency key: the same attempt's key resolves to
  // the same RecurrenceRule, so finishing an attempt whose outcome was never
  // observed cannot create a second series. This is exactly what the one-off
  // path cannot offer (wp7-plan.md §7), and the difference is worth saying out
  // loud on screen rather than only here.
  //
  // **Guarded by the body, not just by the outcome.** The safety is the key's,
  // and `createSeries` keeps a key only for the exact body it was minted for —
  // so if the form has changed since the unobservable attempt, the next submit
  // mints a *fresh* key and can create a second series while the screen is
  // still promising it cannot. The retry is therefore offered only while the
  // form still builds byte-identical bytes to the attempt in question; edit
  // anything and the panel falls back to the one-off path's honest answer
  // (check My Bookings), which is what it should say for a genuinely different
  // request whose predecessor may have committed.
  protected readonly canRetrySeries = computed(
    () =>
      this.mode() === 'recurring' &&
      (this.rejection()?.mayHaveBeenCreated ?? false) &&
      this.formMatchesPendingAttempt(),
  );

  // Whether the form as it stands would send exactly what the attempt still
  // waiting to be finished sent.
  private formMatchesPendingAttempt(): boolean {
    const pending = this.pendingAttempt();
    const resource = this.resource();
    return pending !== null && resource !== null && this.recurrenceBody(resource.id) === pending.body;
  }

  protected onTitleInput(event: Event): void {
    this.title.set((event.target as HTMLInputElement).value);
    // A server message about the title belongs to the title that earned it.
    this.clearServerFieldMessages(['title']);
  }

  protected readonly frequencies = RECURRENCE_FREQUENCIES;

  protected setMode(mode: BookingMode): void {
    this.mode.set(mode);
    // A rejection belongs to the request that earned it; switching modes makes
    // it about a different request entirely.
    this.rejection.set(null);
  }

  protected setFrequency(event: Event): void {
    const frequency = (event.target as HTMLSelectElement).value as RecurrenceFrequency;
    this.updateRecurrence({ frequency });
  }

  protected setIntervalValue(event: Event): void {
    this.updateRecurrence({ intervalValue: numberFrom(event) });
  }

  protected setLocalStartTime(event: Event): void {
    this.updateRecurrence({ localStartTime: stringFrom(event) });
  }

  protected setLocalEndTime(event: Event): void {
    this.updateRecurrence({ localEndTime: stringFrom(event) });
  }

  protected setStartDate(event: Event): void {
    this.updateRecurrence({ startDate: stringFrom(event) });
  }

  protected setEndCondition(endCondition: RecurrenceEndConditionKind): void {
    this.updateRecurrence({ endCondition });
  }

  protected setEndDate(event: Event): void {
    this.updateRecurrence({ endDate: stringFrom(event) });
  }

  protected setOccurrenceCount(event: Event): void {
    this.updateRecurrence({ occurrenceCount: numberFrom(event) });
  }

  // Every edit to the recurring form drops the server's messages about it.
  // Not cosmetic: those messages gate the submit (`recurrenceIsValid`), and a
  // rejection is otherwise only cleared *by* a submit — so a server-only rule
  // the client cannot restate would leave the form permanently unsubmittable
  // with no way for the member to act on the advice it was just given. The
  // whole recurring group is cleared rather than the one patched field,
  // because any edit changes the request body's identity anyway (the same
  // "the body decides what attempt this is" rule `createSeries` works by).
  //
  // The form-level parts of the rejection — including the unknown-outcome
  // warning — deliberately survive: those are about the *previous request*,
  // and an edit here says nothing about whether it committed.
  private updateRecurrence(patch: Partial<RecurrenceFormValue>): void {
    this.recurrence.update((current) => ({ ...current, ...patch }));
    this.clearServerFieldMessages(RECURRENCE_FIELD_NAMES);
  }

  private clearServerFieldMessages(fields: readonly BookingFieldName[]): void {
    this.rejection.update((current) => {
      if (!current) {
        return current;
      }

      const remaining = { ...current.fieldMessages };
      let cleared = false;
      for (const field of fields) {
        if (field in remaining) {
          delete remaining[field];
          cleared = true;
        }
      }

      // The same object back when nothing changed, so an edit to an untouched
      // control doesn't re-notify every computed reading this signal.
      return cleared ? { ...current, fieldMessages: remaining } : current;
    });
  }

  protected incrementQuantity(): void {
    const capacity = this.resource()?.capacity ?? 1;
    this.quantity.update((q) => Math.min(q + 1, capacity));
    this.clearServerFieldMessages(['quantity']);
  }

  protected decrementQuantity(): void {
    this.quantity.update((q) => Math.max(q - 1, 1));
    this.clearServerFieldMessages(['quantity']);
  }

  // FR-4.1. The one write this screen makes.
  //
  // **Never retried, automatically or by a button** — POST /bookings has no
  // idempotency key of any kind (wp7-plan.md §7's flagged gap, owned by a
  // future backend package), so a repeat of a request whose response was lost
  // creates a *second* booking rather than resolving to the first. The
  // `submitting` guard below is the same rule applied to an impatient
  // double-click: the button is disabled while a request is in flight, and
  // this re-checks it for anything that could still reach the method
  // programmatically.
  //
  // The instants go out exactly as they arrived — already whole-second UTC,
  // normalized by parseBookingSelection — which is what
  // CreateBookingCommandRequestValidator requires of both (a zone designator,
  // no fractional seconds).
  protected confirmBooking(): void {
    if (!this.canSubmit()) {
      return;
    }

    if (this.mode() === 'recurring') {
      this.createSeries();
      return;
    }

    const resource = this.resource();
    const selection = this.selection();
    if (!resource || !selection) {
      return;
    }

    this.submitting.set(true);
    this.rejection.set(null);

    const title = this.title().trim();

    this.bookingsService
      .create({
        resourceId: resource.id,
        startsAtUtc: selection.startUtc,
        endsAtUtc: selection.endUtc,
        quantity: this.quantity(),
        // An untitled booking is legal (the column is nullable) — an empty box
        // means "no title", not an empty string.
        title: title.length > 0 ? title : null,
      })
      .subscribe({
        next: (response) => {
          this.created.set(response);
          this.submitting.set(false);
        },
        error: (error: unknown) => {
          const rejection = describeBookingRejection(error);
          this.submitting.set(false);

          // A resource that isn't there any more (or never was, for this
          // caller) isn't a message on a form — it's step 2's own not-found
          // state, which is what the rest of this screen already shows for it.
          if (rejection.resourceNotFound) {
            this.resource.set(null);
            this.notFound.set(true);
            return;
          }

          this.rejection.set(rejection);
        },
      });
  }

  // FR-5.1 / FR-5.4. The recurring counterpart of confirmBooking, and the one
  // write in this app that is genuinely safe to retry.
  //
  // **The idempotency key's lifecycle, spelled out because getting it backwards
  // fails in both directions** (wp7-plan.md step 7): one key per submission
  // *attempt*, reused only when retrying that same attempt after an outcome
  // nobody could observe, and regenerated as soon as the form changes. Reuse it
  // too eagerly and a deliberate second series silently resolves to the first;
  // regenerate it on a retry and a crash-resumed request creates a duplicate
  // series — which is precisely what `RecurrenceCreationOperation` exists to
  // prevent (the 2026-09-15 hardening pass, item 11).
  //
  // "The same attempt" is decided by the request body itself rather than by a
  // dirty flag: if every field is byte-identical to what the failed attempt
  // sent, it *is* that attempt. Edit anything and the body differs, so the next
  // submit mints a fresh key without anything having to remember to.
  private createSeries(): void {
    const resource = this.resource();
    if (!resource) {
      return;
    }

    const submitted = this.recurrence();
    const request = this.recurrenceRequest(resource.id);
    const body = JSON.stringify(request);

    const pending = this.pendingAttempt();
    const idempotencyKey = pending?.body === body ? pending.key : crypto.randomUUID();
    this.pendingAttempt.set({ key: idempotencyKey, body });

    // What the outcome panel will describe. Taken here, from the values this
    // request is built out of, rather than read back off the live form when
    // the response lands.
    this.submittedRecurrence.set(submitted);

    this.submitting.set(true);
    this.rejection.set(null);

    this.recurrenceRulesService.create(request, idempotencyKey).subscribe({
      next: (response) => {
        this.seriesOutcome.set(seriesOutcomeFromResponse(response));
        this.submitting.set(false);
        // A definitive answer: a later submit is a new attempt, not a retry.
        this.pendingAttempt.set(null);
      },
      error: (error: unknown) => {
        this.submitting.set(false);

        // Nothing could be booked. Not a failure to report as one: the 422
        // carries the same per-occurrence breakdown a 201 does, and that
        // breakdown *is* the answer (FR-5.4).
        const refusal = parseSeriesRefusal(error);
        if (refusal) {
          this.seriesOutcome.set(refusal);
          this.pendingAttempt.set(null);
          return;
        }

        // The recurring endpoint's own vocabulary, not the one-off form's:
        // its validation names StartDate/IntervalValue/OccurrenceCount and the
        // rest, none of which the one-off map knows — so every one of them
        // used to arrive as "go back to availability and pick a slot again",
        // pointing at a screen that feeds none of these fields.
        const rejection = describeRecurrenceRejection(error);
        if (rejection.resourceNotFound) {
          this.resource.set(null);
          this.notFound.set(true);
          this.pendingAttempt.set(null);
          return;
        }

        // Only an unobservable outcome keeps the key alive — that is the one
        // case where trying again means "finish that attempt" rather than
        // "start another". Any other refusal created nothing, so the next
        // submit should be a fresh attempt.
        if (!rejection.mayHaveBeenCreated) {
          this.pendingAttempt.set(null);
        }

        this.rejection.set(rejection);
      },
    });
  }

  // The request the recurring form would send right now. One builder, used
  // both to submit and to decide whether a pending attempt is still "this"
  // request — so the two can never disagree about what the body is.
  private recurrenceRequest(resourceId: string) {
    const title = this.title().trim();
    return buildRecurrenceRequest(
      this.recurrence(),
      resourceId,
      this.quantity(),
      title.length > 0 ? title : null,
    );
  }

  private recurrenceBody(resourceId: string): string {
    return JSON.stringify(this.recurrenceRequest(resourceId));
  }

  // Survives across submits on purpose — see createSeries.
  //
  // **A signal rather than a plain field**, because `canRetrySeries` now reads
  // it: the "trying again is safe" promise depends on this attempt still
  // matching the form, which has to be re-evaluated as the form changes.
  //
  // **Deliberately not persisted.** The guarantee is a same-page one: reload
  // the tab, navigate away and back, or crash the browser and this key is
  // gone, so a later submit of the same series mints a new one and could
  // create a second series if the original attempt had in fact committed.
  // Persisting it (sessionStorage, scoped per user and resource) was
  // considered and rejected for this pass: it trades one silent failure for
  // the opposite one — a key that outlives the attempt it belongs to makes a
  // *deliberate* second identical series resolve to the first — and the
  // honest recovery already exists and is already what this app says
  // everywhere else an outcome is unknown. So the limit is stated on screen
  // ("only while this page is open") instead of being papered over.
  private readonly pendingAttempt = signal<{ key: string; body: string } | null>(null);

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
