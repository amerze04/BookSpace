import { LocalDateString } from './availability.models';

// Plain calendar-date arithmetic for the availability screen's date-range
// picker. Deliberately never touches the *browser's* local timezone: every
// function here treats a LocalDateString as bare Y/M/D digits and does its
// math against a UTC-anchored Date purely as a calendar calculator — using
// the browser's own local Date (new Date(y, m - 1, d)) would silently shift
// a date near midnight depending on where the browser happens to be, which
// is exactly the class of bug decision `0003` exists to keep out of this
// screen: availability is the *resource's* local date, not the viewer's.

// The one place "today" is computed for a resource — via Intl's timeZone
// option, formatToParts rather than a locale string trick (en-CA's ISO-like
// output isn't guaranteed by spec, only by convention), so the result is a
// digit-for-digit Y-M-D regardless of the runtime's ICU data.
export function resourceLocalToday(timeZoneId: string): LocalDateString {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: timeZoneId,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date());

  const lookup = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
  return `${lookup('year')}-${lookup('month')}-${lookup('day')}`;
}

export function addDays(dateStr: LocalDateString, days: number): LocalDateString {
  const date = toUtcDate(dateStr);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

// Inclusive of both ends, mirroring AvailabilityQueryRules.RangeLengthInDays
// on the backend exactly — a single date is a range of one day, not zero.
export function rangeLengthDays(fromDateStr: LocalDateString, toDateStr: LocalDateString): number {
  const fromUtc = toUtcDate(fromDateStr).getTime();
  const toUtc = toUtcDate(toDateStr).getTime();
  return Math.round((toUtc - fromUtc) / 86_400_000) + 1;
}

// "2026-09-21" -> "Sep 21, 2026". Formatted with timeZone: 'UTC' so the
// UTC-anchored Date built for display purposes can't be reinterpreted into
// the viewer's own local timezone and drift by a day.
export function formatLocalDate(dateStr: LocalDateString): string {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(toUtcDate(dateStr));
}

// "2026-09-21" -> "Mon, Sep 21" — the grid's own day-row label (step 5),
// distinct from formatLocalDate's "Sep 21, 2026" (no year: a day row is
// always inside the picker's own visible range, so the year would just be
// repeated noise seven times down the grid).
export function formatLocalDateWithWeekday(dateStr: LocalDateString): string {
  return new Intl.DateTimeFormat('en-US', {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(toUtcDate(dateStr));
}

function toUtcDate(dateStr: LocalDateString): Date {
  const [year, month, day] = dateStr.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day));
}

// "2026-09-22" -> "Tuesday, Sep 22" — the full weekday name, for the
// availability grid's accessible segment labels (item 10), where the
// abbreviated one formatLocalDateWithWeekday already uses for the visible
// day-row label reads ambiguously out loud ("Tue" vs "Thu").
export function formatLocalDateWithFullWeekday(dateStr: LocalDateString): string {
  return new Intl.DateTimeFormat('en-US', {
    weekday: 'long',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(toUtcDate(dateStr));
}

// 0 (Sunday) through 6 (Saturday), matching JS's own Date.getUTCDay() —
// used to test a calendar date against AvailabilityWindowDetail.weekday
// (item 6), which names weekdays the same way buildWeekdayRows already
// does. A plain calendar computation, like every other function in this
// file — a LocalDateString names resource-local digits, never the viewer's
// own timezone.
const WEEKDAY_BY_INDEX: readonly string[] = [
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
];

export function weekdayOf(dateStr: LocalDateString): string {
  return WEEKDAY_BY_INDEX[toUtcDate(dateStr).getUTCDay()];
}

// A UTC instant, read as a resource-local calendar date + time-of-day — what
// the grid (step 5) needs to place a `BookableIntervalDetail` under the
// right day row and at the right horizontal position. `hourCycle: 'h23'` is
// explicit rather than relying on `hour12: false`'s default cycle, which is
// not guaranteed to report midnight as "00" rather than "24" — h23 always
// does.
export interface ResourceLocalInstant {
  date: LocalDateString;
  minutesOfDay: number;
}

export function utcToResourceLocal(utcIso: string, timeZoneId: string): ResourceLocalInstant {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: timeZoneId,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(new Date(utcIso));

  const lookup = (type: string) => parts.find((p) => p.type === type)?.value ?? '0';
  const date = `${lookup('year')}-${lookup('month')}-${lookup('day')}`;
  const minutesOfDay = Number(lookup('hour')) * 60 + Number(lookup('minute'));
  return { date, minutesOfDay };
}

// "2026-09-24" -> "Thu, Sep 24, 2026" — the selected-time summary panel's
// own date line (step 6), which includes the year where the day-row label
// deliberately doesn't (formatLocalDateWithWeekday's own comment): a
// selection summary can be read well after the grid it came from has
// scrolled out of view, so the year is worth repeating there.
export function formatLocalDateWithWeekdayAndYear(dateStr: LocalDateString): string {
  return new Intl.DateTimeFormat('en-US', {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(toUtcDate(dateStr));
}

// Signed whole-day difference (b - a) between two LocalDateStrings — for
// code (availability-grid.ts's local->UTC inversion) that needs to
// reconcile a UTC->local reading landing on a different calendar day than
// the one it started from.
export function localDateDiffDays(a: LocalDateString, b: LocalDateString): number {
  return Math.round((toUtcDate(b).getTime() - toUtcDate(a).getTime()) / 86_400_000);
}

// Adds a plain minute offset to a UTC instant — used by availability-grid.ts
// to derive a segment's own UTC boundaries (and a viewer's sub-selection
// within one) from the interval's own known startUtc, by walking forward the
// same number of minutes the local wall clock advanced. See that file's own
// comment on DaySegment for the one case (a DST transition landing inside an
// overnight segment) this doesn't perfectly handle, and why that's accepted.
export function addMinutesToUtc(utcIso: string, minutes: number): string {
  return new Date(new Date(utcIso).getTime() + minutes * 60_000).toISOString();
}

// 480 -> "08:00", 1080 -> "18:00". Deliberately not wrapped mod 1440: a
// segment that runs to local midnight ends at minute 1440, and "24:00" reads
// as unambiguously "end of this day" where "00:00" would look like the day's
// own start.
export function formatMinutesOfDay(minutes: number): string {
  const hh = Math.floor(minutes / 60);
  const mm = minutes % 60;
  return `${String(hh).padStart(2, '0')}:${String(mm).padStart(2, '0')}`;
}
