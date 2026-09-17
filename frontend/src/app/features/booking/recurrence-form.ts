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
  startDate?: string;
  interval?: string;
  times?: string;
  duration?: string;
  endDate?: string;
  occurrenceCount?: string;
}

// ---- Primitive guards, and why they come before anything derived ----
//
// **Every date helper in `local-date.ts` is a calendar calculator over a
// `Date`, and a `Date` that went invalid throws only at the very end** — when
// `toISOString()` is finally called. `addDays('', 7)` and
// `addDays('2026-09-24', 7e18)` both raise `RangeError: Invalid time value`
// rather than returning nonsense, which means a *derived* calculation over an
// unvalidated primitive is not a wrong answer, it is a crash in a `computed`
// the template reads. Two ordinary things a member can do reach it: clearing
// the Start date box (an `<input type="date">` hands back `''`), and typing a
// long number into Repeat every or After N occurrences (the product
// `intervalValue * (occurrenceCount - 1)` is what gets multiplied out).
//
// So the order below is load-bearing, not stylistic: structural primitives
// first, derived date arithmetic only over primitives that already passed.
// The two helpers here are what "already passed" means.

// A calendar date, judged as digits rather than by handing it to `Date` —
// which accepts "2026-02-31" and rolls it into March, and reads a two-digit
// year as 19xx. The form only ever holds an `<input type="date">` value, so
// anything else is a cleared box or a hand-edited one.
const LOCAL_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

// `CreateRecurrenceSeriesCommandRequestValidator.MaxStartDate`
// (`DateOnly.MaxValue.AddYears(-10)`), restated: past this the server's own
// span checks would be the thing that overflows, so it refuses the start date
// outright rather than letting them.
export const MAX_START_DATE: LocalDateString = '9989-12-31';

export function isValidLocalDate(value: string): boolean {
  if (!LOCAL_DATE_PATTERN.test(value)) {
    return false;
  }

  const [year, month, day] = value.split('-').map(Number);
  if (year < 1 || month < 1 || month > 12 || day < 1) {
    return false;
  }

  return day <= daysInMonth(year, month);
}

function daysInMonth(year: number, month: number): number {
  const isLeap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
  return [31, isLeap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
}

// `Number.isSafeInteger`, not `Number.isInteger`: 1e21 *is* an integer by the
// latter, and multiplying two of them past 2^53 gives a silently wrong product
// that then reaches `Date` arithmetic. An empty number box reads as NaN
// (`numberFrom` in the component), which fails this too — so a cleared box
// says "must be a whole number of 1 or more" rather than being treated as a
// zero the server would refuse.
export function isPositiveWholeNumber(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 1;
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

// Eight, not seven: a weekday only recurs on the eighth day. Searching seven
// would miss the resource that is open on exactly one weekday whose window has
// *already closed today* — the one day that could seed a default is then the
// day the search stops one short of.
const DAYS_SEARCHED_FOR_AN_OPEN_SPAN = 8;

// The step the availability grid already offers, reused here so a default
// start time reads like a time somebody would pick ("15:45") rather than like
// the instant the page happened to load ("15:37").
const DEFAULT_START_STEP_MINUTES = 15;

// The resource's own wall clock at the moment the form opens — **both halves
// of it**. The date alone was what this used to take, and it is what made a
// resource open 09:00-17:00 seed a 09:00-10:00 default at 15:30 in its own
// zone: a series whose first occurrence is already in the past, refused
// per-occurrence with `BookingInThePast` after a round trip.
export interface ResourceLocalNow {
  date: LocalDateString;
  minutesOfDay: number;
}

// Why a recurring series cannot be set up against this resource *at all* —
// a fact about its configuration, not about the values in the form.
//
// The distinction that matters (and the one this must not blur): none of these
// mean "fully booked" or "blacked out". Those are answers only the server can
// give, they change by the hour, and a series is expected to collect some of
// them per occurrence (FR-5.4). These three are structural — no request built
// from this form could ever produce a single bookable occurrence — which is
// why they disable the submit instead of being reported against a control.
export type RecurrenceUnavailableReason =
  | 'noOpeningHours'
  | 'durationLimitsConflict'
  | 'noBookableWindow';

export function recurrenceUnavailableReason(
  schedule: ResourceSchedule,
): RecurrenceUnavailableReason | null {
  if (schedule.availabilityWindows.length === 0) {
    return 'noOpeningHours';
  }

  const shortest = shortestLegalDuration(schedule);
  const longest = schedule.maxDurationMinutes;
  if (longest !== null && shortest > longest) {
    return 'durationLimitsConflict';
  }

  const fits = WEEKDAYS.some((weekday) =>
    openSpansForWeekday(weekday, schedule.availabilityWindows).some(
      (span) => span.endMinutes - span.startMinutes >= shortest,
    ),
  );

  return fits ? null : 'noBookableWindow';
}

// `null` means "no minimum configured", never a default — the same false-
// minimum trap the 2026-09-16 pass fixed in `effectiveMinDuration`. One minute
// is the shortest interval the database itself admits (`CK_Bookings_Interval`
// wants a positive one), so it stands for "any positive length will do"
// without inventing a rule the resource never stated.
function shortestLegalDuration(schedule: ResourceSchedule): number {
  return schedule.minDurationMinutes ?? 1;
}

// The form a member gets when they arrive to book a series **without** picking
// a slot first — the entry point that keeps the availability screen from being
// a toll booth on the way to a recurring booking.
//
// Seeded from the resource's own schedule *and its own clock*: the first
// opening span from now on that can actually hold a booking of a length this
// resource allows. `null` when no such span exists inside the next week, which
// is exactly the set of cases `recurrenceUnavailableReason` names — the caller
// shows that reason rather than a form seeded with something that fails its
// own guards.
export function defaultRecurrenceFor(
  schedule: ResourceSchedule,
  nowLocal: ResourceLocalNow,
): RecurrenceFormValue | null {
  const shortest = shortestLegalDuration(schedule);
  const longest = schedule.maxDurationMinutes;
  if (longest !== null && shortest > longest) {
    return null;
  }

  // What to ask for when nothing forces a length: an hour, unless the resource
  // allows less than that (honoured, rather than proposing a duration its own
  // maximum refuses) or requires more.
  let preferred = schedule.minDurationMinutes ?? FALLBACK_DURATION_MINUTES;
  if (longest !== null && preferred > longest) {
    preferred = longest;
  }

  for (let offset = 0; offset < DAYS_SEARCHED_FOR_AN_OPEN_SPAN; offset++) {
    const date = addDays(nowLocal.date, offset);

    // Only today is bounded below by the clock; every later day starts at its
    // own opening time.
    const earliest = offset === 0 ? roundUpToStep(nowLocal.minutesOfDay) : 0;

    for (const span of openSpansForWeekday(weekdayOf(date), schedule.availabilityWindows)) {
      const startMinutes = Math.max(span.startMinutes, earliest);
      const remaining = span.endMinutes - startMinutes;
      if (remaining < shortest) {
        continue;
      }

      // Shortened to what is actually left of the span rather than allowed to
      // run past closing — and never below the minimum, which the check above
      // already guaranteed there is room for.
      const duration = Math.min(preferred, remaining);

      return {
        ...BASE_DEFAULTS,
        startDate: date,
        localStartTime: formatMinutesOfDay(startMinutes),
        localEndTime: formatMinutesOfDay(startMinutes + duration),
        endDate: addDays(date, 28),
      };
    }
  }

  return null;
}

function roundUpToStep(minutes: number): number {
  return Math.ceil(minutes / DEFAULT_START_STEP_MINUTES) * DEFAULT_START_STEP_MINUTES;
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

  // ---- Structural primitives first ----
  //
  // Nothing below this block may feed a date helper a value these two checks
  // have not already accepted: see the header on `isValidLocalDate` for what
  // happens when one does.

  const startDateIsUsable = isValidLocalDate(value.startDate);
  if (!startDateIsUsable) {
    errors.startDate = value.startDate
      ? 'Enter a valid start date (YYYY-MM-DD).'
      : 'Choose the date the series starts on.';
  } else if (value.startDate > MAX_START_DATE) {
    // `CreateRecurrenceSeriesCommandRequestValidator`'s own MaxStartDate,
    // restated — past it the server's span checks are what overflow.
    errors.startDate = `The start date must be on or before ${MAX_START_DATE}.`;
  }

  // CK_RecurrenceRules_Interval, restated: the validator refuses anything but
  // a positive value, and the domain constructor guards it again. Bounded
  // above only by what the two-year span check below can safely compute with,
  // exactly as the server bounds it — item 12 of the 2026-09-15 pass removed
  // the per-field proxy there and this must not reintroduce one.
  const intervalIsUsable = isPositiveWholeNumber(value.intervalValue);
  if (!intervalIsUsable) {
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

    // Only asked once the start date is a real date: the weekly arm reads its
    // weekday off it.
    const closed = startDateIsUsable
      ? outsideOpeningHours(value, schedule.availabilityWindows)
      : null;
    if (closed) {
      errors.times = closed;
    }
  }

  if (value.endCondition === 'endDate') {
    if (!value.endDate) {
      errors.endDate = 'Choose the date the series ends on.';
    } else if (!isValidLocalDate(value.endDate)) {
      errors.endDate = 'Enter a valid end date (YYYY-MM-DD).';
    } else if (startDateIsUsable) {
      // Compared against the start date only once that is itself a date —
      // otherwise `'2026-10-01' < ''` is false and a cleared start date would
      // pass this arm with no error at all, leaving a request the server can
      // only answer with a 400.
      if (value.endDate < value.startDate) {
        errors.endDate = 'The end date must not be before the start date.';
      } else if (!isWithinMaxSpan(value.startDate, value.endDate)) {
        errors.endDate = `A series can run for at most ${MAX_RECURRENCE_SPAN_YEARS} years.`;
      }
    }
  } else if (!isPositiveWholeNumber(value.occurrenceCount)) {
    errors.occurrenceCount = 'Number of occurrences must be a whole number of 1 or more.';
  } else if (
    intervalIsUsable &&
    startDateIsUsable &&
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

// `IsWithinMaxSpan`, restated — including its own refusal to compute a date it
// cannot represent. The server answers "no" for a start date within two years
// of `DateOnly.MaxValue` rather than letting `AddYears` throw; this answers
// "no" for any start date it was not given, for the same reason.
export function isWithinMaxSpan(startDate: LocalDateString, endDate: LocalDateString): boolean {
  if (!isValidLocalDate(startDate) || !isValidLocalDate(endDate) || startDate > MAX_START_DATE) {
    return false;
  }
  return endDate <= addYears(startDate, MAX_RECURRENCE_SPAN_YEARS);
}

// The day count past which the implied span is refused without computing it —
// `IsOccurrenceCountWithinMaxSpan`'s own 10,000-day guard, and for the same
// reason: it is what keeps a large interval/count pair from ever reaching
// date arithmetic. Deliberately far wider than two years, so it never decides
// a case the exact comparison below could have decided.
const MAX_IMPLIED_DAYS = 10_000;

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
  if (!canComputeImpliedEndDate(frequency, intervalValue, startDate, occurrenceCount)) {
    // Not computable *is* "past the cap" as far as this question goes: a pair
    // the day guard refuses is orders of magnitude past two years, and one
    // that is not a pair of numbers at all has its own error already.
    return false;
  }

  return impliedEndDate(frequency, intervalValue, startDate, occurrenceCount) <= addYears(startDate, MAX_RECURRENCE_SPAN_YEARS);
}

// Whether `impliedEndDate` can safely be asked at all — the guard that stands
// between a number box and `Date` arithmetic, and the client's counterpart to
// the server doing its own multiplication in `long` space before touching
// `DateOnly`.
//
// `isSafeInteger` on the *product*, not just the operands: 1e15 and 1e15 are
// each safe integers whose product is not, and an unsafe product is silently
// wrong here rather than throwing the way C#'s `checked` would.
export function canComputeImpliedEndDate(
  frequency: RecurrenceFrequency,
  intervalValue: number,
  startDate: LocalDateString,
  occurrenceCount: number,
): boolean {
  if (!isValidLocalDate(startDate) || startDate > MAX_START_DATE) {
    return false;
  }

  if (!isPositiveWholeNumber(intervalValue) || !isPositiveWholeNumber(occurrenceCount)) {
    return false;
  }

  const steps = intervalValue * (occurrenceCount - 1);
  const impliedDays = frequency === 'Weekly' ? steps * 7 : steps;
  return Number.isSafeInteger(impliedDays) && impliedDays >= 0 && impliedDays <= MAX_IMPLIED_DAYS;
}

// `RecurrenceRule.ComputeImpliedEndDate`, restated. Also what the summary line
// shows a member, so the date they read is the date the rule actually implies.
//
// **Takes validated inputs only** — a real start date, a safe-integer interval
// and count. It is plain `Date` arithmetic underneath, which throws on
// anything else rather than returning a wrong answer, so every caller checks
// first: `isOccurrenceCountWithinMaxSpan` above, and the component's own
// summary label.
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
