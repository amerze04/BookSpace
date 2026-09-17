import { LocalDateString } from '../availability/availability.models';
import { addDays, addMonths, addYears, formatMinutesOfDay, weekdayOf } from '../availability/local-date';
import { MinuteSpan, OpeningWindow, openSpansForWeekday } from '../availability/availability-grid';
import { CreateRecurrenceSeriesRequest, RecurrenceFrequency } from './recurrence.models';

// The recurring half of the booking form: its shape, its guards, and the
// request it builds. Kept out of the component because none of it is
// Angular-specific and because the span arithmetic below has to be checked
// against the server's, which is much easier to do — and to test — in one
// place.
//
// **Every guard here mirrors one the backend already enforces.** The point is
// not to be the authority (it cannot be: the handler re-checks everything with
// the resource, the clock and the calendar in hand) but to tell the member
// immediately what a 400 would otherwise tell them a round trip later, and to
// make sure the request this form builds is one the API could accept. Phase 2
// took the same approach with `AvailabilityQueryRules.MaxRangeDays`.

// Decision `0007`: a series is materialized up front, capped at two years.
// `CK_RecurrenceRules_MaxSpan` enforces it in the database, and
// CreateRecurrenceSeriesCommandRequestValidator restates it as a 400 — this is
// the third statement of the same number, and the comments on all three say so.
export const MAX_RECURRENCE_SPAN_YEARS = 2;

// Which end condition the radio has selected. The two are mutually exclusive
// on the wire (`CK_RecurrenceRules_EndCondition`: exactly one of EndDate or
// OccurrenceCount), and modelling the *choice* rather than two independently
// fillable fields is what makes "both" and "neither" unrepresentable here
// rather than merely validated against.
export type RecurrenceEndConditionKind = 'endDate' | 'occurrenceCount';

export interface RecurrenceFormValue {
  frequency: RecurrenceFrequency;
  intervalValue: number;
  // "HH:mm", the value an <input type="time"> holds. Converted to the wire's
  // "HH:mm:ss" only when the request is built.
  localStartTime: string;
  localEndTime: string;
  startDate: LocalDateString;
  endCondition: RecurrenceEndConditionKind;
  endDate: LocalDateString;
  occurrenceCount: number;
}

export interface RecurrenceFormErrors {
  interval?: string;
  times?: string;
  duration?: string;
  endDate?: string;
  occurrenceCount?: string;
}

// What the form needs to know about the resource to judge a series: its
// duration limits and its weekly opening hours. Both are already on the
// detail read this screen loads, so none of this costs a request.
export interface ResourceSchedule {
  minDurationMinutes: number | null;
  maxDurationMinutes: number | null;
  availabilityWindows: readonly OpeningWindow[];
}

export function recurrenceFormDurationMinutes(value: RecurrenceFormValue): number {
  return minutesOfDay(value.localEndTime) - minutesOfDay(value.localStartTime);
}

// Weekly with an occurrence count is the ordinary standing-meeting case, and a
// count is easier to reason about than an end date — but both are one radio
// click apart and neither is imposed: `CK_RecurrenceRules_EndCondition`
// requires exactly one arm, not a particular one.
const BASE_DEFAULTS: Omit<RecurrenceFormValue, 'startDate' | 'localStartTime' | 'localEndTime' | 'endDate'> = {
  frequency: 'Weekly',
  intervalValue: 1,
  endCondition: 'occurrenceCount',
  occurrenceCount: 4,
};

const FALLBACK_DURATION_MINUTES = 60;
const DAYS_SEARCHED_FOR_AN_OPEN_DAY = 7;

// The form a member gets when they arrive to book a series **without** picking
// a slot first — the entry point that keeps the availability screen from being
// a toll booth on the way to a recurring booking.
//
// Seeded from the resource's own schedule rather than from nothing: the first
// upcoming date whose weekday the resource is actually open on, starting at
// that day's opening time, for the shortest length the resource allows. So the
// form opens on something plausible and valid instead of on a blank that is
// guaranteed to fail its own guards.
export function defaultRecurrenceFor(
  schedule: ResourceSchedule,
  todayLocal: LocalDateString,
): RecurrenceFormValue {
  const startDate = firstOpenDateFrom(todayLocal, schedule.availabilityWindows);
  const openSpans = openSpansForWeekday(weekdayOf(startDate), schedule.availabilityWindows);
  const opensAt = openSpans[0]?.startMinutes ?? 9 * 60;
  const closesAt = openSpans[0]?.endMinutes ?? 17 * 60;

  const duration = schedule.minDurationMinutes ?? FALLBACK_DURATION_MINUTES;
  const endMinutes = Math.min(opensAt + duration, closesAt);

  return {
    ...BASE_DEFAULTS,
    startDate,
    localStartTime: formatMinutesOfDay(opensAt),
    localEndTime: formatMinutesOfDay(endMinutes),
    endDate: addDays(startDate, 28),
  };
}

// Falls back to today when the resource has no windows at all — there is no
// open day to find, and the form's own guards then say so rather than this
// silently searching forever.
function firstOpenDateFrom(
  todayLocal: LocalDateString,
  windows: readonly OpeningWindow[],
): LocalDateString {
  for (let offset = 0; offset < DAYS_SEARCHED_FOR_AN_OPEN_DAY; offset++) {
    const candidate = addDays(todayLocal, offset);
    if (openSpansForWeekday(weekdayOf(candidate), windows).length > 0) {
      return candidate;
    }
  }
  return todayLocal;
}

// The same defaults, seeded from a slot the member *did* pick on the
// availability screen — which stays the better on-ramp when they want one: it
// fixes the weekday and a time of day the resource is provably open at, so the
// whole series inherits a schedule that can actually succeed.
export function recurrenceFromSelection(
  startDateLocal: LocalDateString,
  startMinutes: number,
  endMinutes: number,
): RecurrenceFormValue {
  return {
    ...BASE_DEFAULTS,
    startDate: startDateLocal,
    localStartTime: formatMinutesOfDay(startMinutes),
    localEndTime: formatMinutesOfDay(endMinutes),
    endDate: addDays(startDateLocal, 28),
  };
}

export function validateRecurrenceForm(
  value: RecurrenceFormValue,
  schedule: ResourceSchedule,
): RecurrenceFormErrors {
  const limits = schedule;
  const errors: RecurrenceFormErrors = {};

  // CK_RecurrenceRules_Interval, restated: the validator refuses anything but
  // a positive value, and the domain constructor guards it again.
  if (!Number.isInteger(value.intervalValue) || value.intervalValue < 1) {
    errors.interval = 'Repeat every must be a whole number of 1 or more.';
  }

  // Not backed by a database constraint, but the validator and
  // RecurrenceRule's own constructor both require it.
  const duration = recurrenceFormDurationMinutes(value);
  if (!(duration > 0)) {
    errors.times = 'The end time must be after the start time.';
  } else {
    // The handler asks Resource.AllowsBookingDuration once for the whole
    // series — every occurrence shares this nominal length — and refuses with
    // BookingDurationOutOfRange, so the form asks the same question first.
    if (limits.minDurationMinutes !== null && duration < limits.minDurationMinutes) {
      errors.duration = `This resource requires bookings of at least ${limits.minDurationMinutes} minutes.`;
    } else if (limits.maxDurationMinutes !== null && duration > limits.maxDurationMinutes) {
      errors.duration = `This resource allows bookings of at most ${limits.maxDurationMinutes} minutes.`;
    }

    const closed = outsideOpeningHours(value, schedule.availabilityWindows);
    if (closed) {
      errors.times = closed;
    }
  }

  if (value.endCondition === 'endDate') {
    if (!value.endDate) {
      errors.endDate = 'Choose the date the series ends on.';
    } else if (value.endDate < value.startDate) {
      errors.endDate = 'The end date must not be before the start date.';
    } else if (!isWithinMaxSpan(value.startDate, value.endDate)) {
      errors.endDate = `A series can run for at most ${MAX_RECURRENCE_SPAN_YEARS} years.`;
    }
  } else if (!Number.isInteger(value.occurrenceCount) || value.occurrenceCount < 1) {
    errors.occurrenceCount = 'Number of occurrences must be a whole number of 1 or more.';
  } else if (
    value.intervalValue >= 1 &&
    !isOccurrenceCountWithinMaxSpan(value.frequency, value.intervalValue, value.startDate, value.occurrenceCount)
  ) {
    // The bug item 12 of the 2026-09-15 hardening pass fixed server-side was
    // exactly this check being approximated per field instead of applied to
    // what the two imply together — so the client does not approximate it
    // either.
    errors.occurrenceCount = `That many occurrences at this interval would run past the ${MAX_RECURRENCE_SPAN_YEARS}-year limit.`;
  }

  return errors;
}

export function hasRecurrenceErrors(errors: RecurrenceFormErrors): boolean {
  return Object.keys(errors).length > 0;
}

// **The one guard that earns its keep most**, and the reason the times can stay
// editable at all: a series whose time of day falls outside the resource's
// opening hours has *every* occurrence refused with `OutsideAvailability`, and
// the member finds out only after filling in the whole form and waiting for a
// 422. The windows are already loaded on this screen, so the answer is free.
//
// What it does **not** claim: that the occurrences are free. Nothing client-side
// can know that — a series books dates no availability query was ever asked
// about, and FR-5.4's per-occurrence report is the designed answer to it. This
// only rules out the case that cannot possibly succeed.
//
// Frequency decides which weekdays to test, because that is what decides where
// the occurrences land:
//   Weekly  -> every occurrence shares the start date's weekday, so that one
//              weekday's hours are exactly the question.
//   Daily   -> occurrences land on every weekday, and a Mon-Fri resource
//   Monthly -> or a varying day-of-month will legitimately refuse some of them.
//              So the only unrecoverable case is a time that fits *no* weekday,
//              and partial refusals are left to the report.
function outsideOpeningHours(
  value: RecurrenceFormValue,
  windows: readonly OpeningWindow[],
): string | null {
  if (windows.length === 0 || !value.startDate) {
    return null;
  }

  const span = {
    startMinutes: minutesOfDay(value.localStartTime),
    endMinutes: minutesOfDay(value.localEndTime),
  };

  if (value.frequency === 'Weekly') {
    const weekday = weekdayOf(value.startDate);
    return fitsInsideAnySpan(span, openSpansForWeekday(weekday, windows))
      ? null
      : `This resource isn't open then on ${weekday}s. Pick a time inside its hours, or a different start date.`;
  }

  const fitsSomeWeekday = WEEKDAYS.some((weekday) =>
    fitsInsideAnySpan(span, openSpansForWeekday(weekday, windows)),
  );

  return fitsSomeWeekday
    ? null
    : "This resource isn't open at that time on any day, so every occurrence would be refused.";
}

const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

// Wholly inside one span, not merely overlapping one: "partly open is not
// open" — a booking running past closing is refused, never truncated
// (ReasonCodes.OutsideAvailability).
function fitsInsideAnySpan(span: MinuteSpan, openSpans: readonly MinuteSpan[]): boolean {
  return openSpans.some(
    (open) => span.startMinutes >= open.startMinutes && span.endMinutes <= open.endMinutes,
  );
}

// `IsWithinMaxSpan`, restated.
export function isWithinMaxSpan(startDate: LocalDateString, endDate: LocalDateString): boolean {
  return endDate <= addYears(startDate, MAX_RECURRENCE_SPAN_YEARS);
}

// `IsOccurrenceCountWithinMaxSpan`, restated — the last occurrence is
// `intervalValue * (occurrenceCount - 1)` steps after the start, and a step is
// a day, a week or a month depending on the frequency (RecurrenceRule.StepDate
// multiplies Weekly's by 7 internally, which is why the week case is days).
export function isOccurrenceCountWithinMaxSpan(
  frequency: RecurrenceFrequency,
  intervalValue: number,
  startDate: LocalDateString,
  occurrenceCount: number,
): boolean {
  return impliedEndDate(frequency, intervalValue, startDate, occurrenceCount) <= addYears(startDate, MAX_RECURRENCE_SPAN_YEARS);
}

// `RecurrenceRule.ComputeImpliedEndDate`, restated. Also what the summary line
// shows a member, so the date they read is the date the rule actually implies.
export function impliedEndDate(
  frequency: RecurrenceFrequency,
  intervalValue: number,
  startDate: LocalDateString,
  occurrenceCount: number,
): LocalDateString {
  const steps = intervalValue * (occurrenceCount - 1);
  switch (frequency) {
    case 'Daily':
      return addDays(startDate, steps);
    case 'Weekly':
      return addDays(startDate, steps * 7);
    case 'Monthly':
      return addMonths(startDate, steps);
  }
}

// The wire body. The end condition sends **exactly one** of its two fields —
// the type system enforces it (`RecurrenceEndCondition`), and the JSON then
// carries only the one that is set.
export function buildRecurrenceRequest(
  value: RecurrenceFormValue,
  resourceId: string,
  quantity: number,
  title: string | null,
): CreateRecurrenceSeriesRequest {
  const base = {
    resourceId,
    frequency: value.frequency,
    intervalValue: value.intervalValue,
    // TimeOnly's own JSON format is "HH:mm:ss"; an <input type="time"> holds
    // "HH:mm". The full three-part form is always sent (step 1 confirmed the
    // API accepts both, so this depends on nothing changing about the short
    // one).
    localStartTime: toWireTime(value.localStartTime),
    localEndTime: toWireTime(value.localEndTime),
    startDate: value.startDate,
    quantity,
    title,
  };

  return value.endCondition === 'endDate'
    ? { ...base, endDate: value.endDate }
    : { ...base, occurrenceCount: value.occurrenceCount };
}

export function toWireTime(time: string): string {
  const [hours = '00', minutes = '00', seconds = '00'] = time.split(':');
  return `${hours.padStart(2, '0')}:${minutes.padStart(2, '0')}:${seconds.padStart(2, '0')}`;
}

function minutesOfDay(time: string): number {
  const [hours, minutes] = time.split(':').map(Number);
  if (Number.isNaN(hours) || Number.isNaN(minutes)) {
    return NaN;
  }
  return hours * 60 + minutes;
}
