import { LocalDateString } from '../../availability/models/availability.models';
import { addDays } from '../../availability/date/local-date';
import { BookingStatus, BookingSummary } from '../../booking/models/booking.models';
import { QueryParamSource } from '../../booking/arrival/booking-arrival';

// The calendar's own arithmetic, kept out of the component for the same reason
// `availability-grid.ts` is: none of it is Angular-specific, and the parts most
// likely to be wrong (week boundaries, a booking that crosses local midnight,
// the UTC window to actually fetch) are worth testing without a TestBed.
//
// **This file reads instants in the *viewer's* timezone, not a resource's** —
// wp7-plan.md §3's display default. Decision `0003` governs the availability
// *question* ("Monday 9am" means what the room's clock says); once a booking is
// a concrete UTC instant, showing it in the viewer's own local time is the
// ordinary calendar convention, and neither `ListBookingsQueryResponse` nor
// `GetBookingQueryResponse` carries a timezone id that would let the UI do
// otherwise without a lookup per row.
//
// That is why this file uses the browser's own local `Date` constructor, which
// `local-date.ts` deliberately never does. The two are not in conflict: that
// file handles *resource*-local dates, where reading them through the browser's
// zone would silently shift a date near midnight — the exact bug decision
// `0003` exists to prevent. Here the viewer's zone *is* the browser's zone, so
// `new Date(y, m - 1, d)` is precisely the right tool and the runtime resolves
// its own DST rules rather than this client inventing any.

export const CALENDAR_VIEWS = ['month', 'week'] as const;
export type CalendarView = (typeof CALENDAR_VIEWS)[number];

const VIEW_PARAM = 'view';
const DATE_PARAM = 'date';

// Which statuses the calendar draws (wp7-plan.md, Phase 4).
//
// `Cancelled` and `Rejected` are absent deliberately: neither holds any time,
// so neither has a cell to occupy, and the member is told by email either way
// (`NotificationKind.Cancelled` / `Rejected` / `SeriesCancelled`). The cost —
// a cancellation's reason becoming reachable only by direct link to
// `/bookings/:id` — was accepted by the owner on 2026-09-18.
//
// **This is a rendering rule, not a query parameter, and it cannot be anything
// else**: `ListBookingsQueryRequest.Status` takes one value, not a set, so
// "everything except cancelled" is not expressible server-side. The window is
// already bounded by the visible dates, so filtering here costs one pass over
// rows that had to be fetched regardless.
const DRAWN_STATUSES: readonly BookingStatus[] = ['Confirmed', 'Pending', 'Completed', 'NoShow'];

export function isDrawnOnCalendar(status: BookingStatus): boolean {
  return DRAWN_STATUSES.includes(status);
}

// ---------------------------------------------------------------------------
// The URL contract
// ---------------------------------------------------------------------------

export interface CalendarUrlState {
  view: CalendarView;
  date: LocalDateString;
}

// `?view=month|week&date=YYYY-MM-DD`, the same "cross-screen state lives in the
// URL" rule Phase 2's date navigator and Phase 3's selected slot both follow: a
// month is shareable, bookmarkable, survives a reload and works with
// back/forward.
//
// Anything unparseable falls back to today's month rather than erroring. A
// calendar has no invalid state to report — every date is a date someone could
// legitimately navigate to — so a bad parameter is a typo to absorb, not a
// failure to render.
export function parseCalendarUrlState(
  params: QueryParamSource,
  today: LocalDateString,
): CalendarUrlState {
  const view = params.get(VIEW_PARAM);
  const date = params.get(DATE_PARAM);

  return {
    view: view === 'week' ? 'week' : 'month',
    date: isLocalDateString(date) ? date : today,
  };
}

export function buildCalendarQueryParams(state: CalendarUrlState): Record<string, string> {
  return { [VIEW_PARAM]: state.view, [DATE_PARAM]: state.date };
}

// Strict, not lenient: `new Date('2026-13-45')` is `Invalid Date` but
// `'2026-9-1'` would parse into something plausible and then render a month the
// URL did not name. Digits are checked before any `Date` is constructed, for
// the same reason `validateRecurrenceForm` orders its own checks that way —
// `local-date.ts` is `Date` arithmetic underneath and throws `RangeError` on an
// invalid one.
function isLocalDateString(value: string | null): value is LocalDateString {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }

  const [year, month, day] = value.split('-').map(Number);
  if (month < 1 || month > 12 || day < 1) {
    return false;
  }

  return day <= new Date(Date.UTC(year, month, 0)).getUTCDate();
}

// ---------------------------------------------------------------------------
// Visible range
// ---------------------------------------------------------------------------

export interface CalendarRange {
  // Both ends inclusive, both viewer-local calendar dates.
  start: LocalDateString;
  end: LocalDateString;
}

// **Monday-first**, matching both calendar designs and the resource detail
// screen's own bookable-hours table, rather than `DayOfWeek`'s Sunday-first
// ordering. The backend enum is a storage detail; week shape is a presentation
// one, and this app has already picked Monday everywhere a person reads it.
export function startOfWeek(date: LocalDateString): LocalDateString {
  // getUTCDay on a UTC-anchored date: this is pure calendar arithmetic on the
  // digits, with no instant involved, so the browser's zone cannot shift it.
  const weekday = utcAnchored(date).getUTCDay();
  const daysSinceMonday = (weekday + 6) % 7;
  return addDays(date, -daysSinceMonday);
}

export function startOfMonth(date: LocalDateString): LocalDateString {
  return `${date.slice(0, 7)}-01`;
}

export function endOfMonth(date: LocalDateString): LocalDateString {
  const [year, month] = date.split('-').map(Number);
  const lastDay = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return `${date.slice(0, 7)}-${String(lastDay).padStart(2, '0')}`;
}

// The dates a view actually puts on screen — for a month, the whole weeks that
// cover it, so the grid is always a rectangle.
//
// **As many weeks as the month needs, not a fixed six.** September 2026 spans
// Mon Aug 31 to Sun Oct 4 — five rows, which is what the design shows. Padding
// every month to six rows would add a blank trailing week to most of them and
// buy nothing but a constant row height.
export function visibleRange(view: CalendarView, anchor: LocalDateString): CalendarRange {
  if (view === 'week') {
    const start = startOfWeek(anchor);
    return { start, end: addDays(start, 6) };
  }

  const start = startOfWeek(startOfMonth(anchor));
  const lastDayOfMonth = endOfMonth(anchor);
  return { start, end: addDays(startOfWeek(lastDayOfMonth), 6) };
}

// Stepping is by the *unit*, not by the visible range: a month step from
// September lands in October even though the visible range started in August.
export function stepRange(
  view: CalendarView,
  anchor: LocalDateString,
  direction: -1 | 1,
): LocalDateString {
  if (view === 'week') {
    return addDays(anchor, direction * 7);
  }

  // Anchored to the 1st before stepping, so a month step from the 31st cannot
  // land on a clamped date and then drift on the next step (Jan 31 -> Feb 28 ->
  // Mar 28 rather than Mar 31). A month view only ever needs to know which
  // month it is in.
  const [year, month] = startOfMonth(anchor).split('-').map(Number);
  const zeroBased = month - 1 + direction;
  const targetYear = year + Math.floor(zeroBased / 12);
  const targetMonth = ((zeroBased % 12) + 12) % 12;
  return `${String(targetYear).padStart(4, '0')}-${String(targetMonth + 1).padStart(2, '0')}-01`;
}

export function datesInRange(range: CalendarRange): LocalDateString[] {
  const dates: LocalDateString[] = [];
  for (let date = range.start; date <= range.end; date = addDays(date, 1)) {
    dates.push(date);
  }
  return dates;
}

// ---------------------------------------------------------------------------
// Viewer-local instants
// ---------------------------------------------------------------------------

export function viewerToday(now: Date = new Date()): LocalDateString {
  return toLocalDateString(now);
}

export function viewerTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

// The viewer-local calendar date an instant falls on.
export function localDateOf(utcIso: string): LocalDateString {
  return toLocalDateString(new Date(utcIso));
}

// Minutes since viewer-local midnight. Can exceed 1440 only if callers pass an
// instant from a later day, which they do not — `entriesForDay` clips first.
export function localMinutesOf(utcIso: string): number {
  const date = new Date(utcIso);
  return date.getHours() * 60 + date.getMinutes();
}

// The UTC window to actually ask the API for: viewer-local midnight opening the
// first visible day, through viewer-local midnight closing the last.
//
// `from`/`to` are an **overlap** filter server-side, so a booking straddling
// either edge is still returned — which is what the month grid's leading and
// trailing days need. Both carry a zone designator because
// `ListBookingsQueryRequestValidator` refuses an `Unspecified` instant outright
// (a filter window silently shifted by a guessed zone is a wrong answer with no
// error), and `to` is strictly after `from` because an equal pair is refused
// too.
export function fetchWindowUtc(range: CalendarRange): { from: string; to: string } {
  return {
    from: localMidnightUtcIso(range.start),
    to: localMidnightUtcIso(addDays(range.end, 1)),
  };
}

function localMidnightUtcIso(date: LocalDateString): string {
  const [year, month, day] = date.split('-').map(Number);
  // Local midnight as a real instant, resolved through the browser's own zone
  // and its own DST rules. On a spring-forward date where 00:00 does not exist,
  // the runtime rolls forward to 01:00 — which is correct for a query window:
  // it is the first instant of that local day that exists.
  return new Date(year, month - 1, day).toISOString().replace(/\.\d{3}Z$/, 'Z');
}

function toLocalDateString(date: Date): LocalDateString {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(
    date.getDate(),
  ).padStart(2, '0')}`;
}

function utcAnchored(date: LocalDateString): Date {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day));
}

// ---------------------------------------------------------------------------
// Laying bookings into the grid
// ---------------------------------------------------------------------------

// One booking as it appears on one day. A booking that crosses viewer-local
// midnight produces one of these per day it touches, each clipped to that day,
// which is why `startMinutes`/`endMinutes` are day-relative rather than read
// off the booking.
export interface DayEntry {
  booking: BookingSummary;
  startMinutes: number;
  endMinutes: number;
  // True when this entry is a clipped piece of a longer booking, so a chip can
  // say so rather than claiming the booking ends at midnight.
  continuesFromPreviousDay: boolean;
  continuesIntoNextDay: boolean;
}

export interface DayCell {
  date: LocalDateString;
  dayOfMonth: number;
  isToday: boolean;
  // Month view only: a leading/trailing day belonging to an adjacent month.
  inAnchoredMonth: boolean;
  entries: DayEntry[];
}

// Bookings the calendar will not draw are dropped here rather than at the
// component, so every consumer of a `DayCell` sees the same set and no screen
// can accidentally render a cancelled booking by reading the raw response.
export function buildDayCells(
  bookings: readonly BookingSummary[],
  range: CalendarRange,
  anchor: LocalDateString,
  today: LocalDateString,
): DayCell[] {
  const anchoredMonth = anchor.slice(0, 7);
  const drawn = bookings.filter((b) => isDrawnOnCalendar(b.status));

  return datesInRange(range).map((date) => ({
    date,
    dayOfMonth: Number(date.slice(8, 10)),
    isToday: date === today,
    inAnchoredMonth: date.slice(0, 7) === anchoredMonth,
    entries: entriesForDay(drawn, date),
  }));
}

// Sorted by start, then by the longer booking first so a short one is never
// buried under a long one that began at the same minute.
function entriesForDay(bookings: readonly BookingSummary[], date: LocalDateString): DayEntry[] {
  const entries: DayEntry[] = [];

  for (const booking of bookings) {
    const startDate = localDateOf(booking.startsAtUtc);
    const endDate = localDateOf(booking.endsAtUtc);

    // An end exactly at local midnight belongs to the day that just closed, not
    // to the one opening — otherwise a 22:00-00:00 booking would also occupy a
    // zero-length sliver of the next day.
    const endsAtMidnight = localMinutesOf(booking.endsAtUtc) === 0;
    const lastDate = endsAtMidnight && endDate > startDate ? addDays(endDate, -1) : endDate;

    if (date < startDate || date > lastDate) {
      continue;
    }

    const continuesFromPreviousDay = date > startDate;
    const continuesIntoNextDay = date < lastDate;

    entries.push({
      booking,
      startMinutes: continuesFromPreviousDay ? 0 : localMinutesOf(booking.startsAtUtc),
      endMinutes: continuesIntoNextDay ? 24 * 60 : localMinutesOf(booking.endsAtUtc) || 24 * 60,
      continuesFromPreviousDay,
      continuesIntoNextDay,
    });
  }

  return entries.sort(
    (a, b) => a.startMinutes - b.startMinutes || b.endMinutes - a.endMinutes,
  );
}

// ---------------------------------------------------------------------------
// How many chips a month cell draws
// ---------------------------------------------------------------------------

// The cap exists to bound the DOM, not to tidy the layout: "rendering stays
// cheap by only building DOM for the currently-visible range" is the
// responsiveness acceptance criterion, and an unbounded stack of chips on a busy
// day defeats it however small the fetch was. A month of 400 bookings must cost
// the same to render as a month of 40.
export const MAX_CHIPS_PER_DAY = 3;

export interface DayChips {
  shown: DayEntry[];
  hiddenCount: number;
}

// When something has to be hidden, the summary row takes one chip's place rather
// than being added below the full set — otherwise a cell showing "the first
// three of four" would be exactly as tall as one showing all four, and the cap
// would buy nothing on the day it matters.
export function chipsForCell(
  entries: readonly DayEntry[],
  expanded: boolean,
  max: number = MAX_CHIPS_PER_DAY,
): DayChips {
  if (expanded || entries.length <= max) {
    return { shown: [...entries], hiddenCount: 0 };
  }

  return {
    shown: entries.slice(0, max - 1),
    hiddenCount: entries.length - (max - 1),
  };
}

// ---------------------------------------------------------------------------
// The week view's hour axis
// ---------------------------------------------------------------------------

export interface HourRange {
  startHour: number;
  endHour: number;
}

// The design draws 08:00-18:00. That is the right *default* and the wrong
// *rule*: a booking at 06:00 — a viewer in a different timezone from the
// resource is enough to produce one — would be drawn outside the grid and so be
// invisible, which is a worse failure than a taller grid. So the window is the
// design's hours, widened to whatever the week actually contains.
export const DEFAULT_WEEK_HOURS: HourRange = { startHour: 8, endHour: 18 };

export function weekHourRange(cells: readonly DayCell[]): HourRange {
  let startHour = DEFAULT_WEEK_HOURS.startHour;
  let endHour = DEFAULT_WEEK_HOURS.endHour;

  for (const cell of cells) {
    for (const entry of cell.entries) {
      startHour = Math.min(startHour, Math.floor(entry.startMinutes / 60));
      endHour = Math.max(endHour, Math.ceil(entry.endMinutes / 60));
    }
  }

  return { startHour, endHour: Math.max(endHour, startHour + 1) };
}

// **The one place a time becomes a vertical position.** Both the hour labels
// down the left and the chips inside a column go through this, so they are
// resolved against the same basis and cannot drift apart.
//
// That mattered: the first version positioned chips as a percentage of the hour
// *span* (08:00-18:00 = 10 hours) while the grid drew one row per *label*
// (08:00...18:00 = 11 rows), so a chip's `top: 20%` resolved against a
// container an hour taller than the window it was computed from and every chip
// sat progressively lower than its own stated time. Found by the owner looking
// at the screen, not by the suite — a percentage string is identical under both
// readings, so nothing short of real layout could have caught it.
export function minuteOffsetPercent(minutes: number, hours: HourRange): number {
  const windowMinutes = (hours.endHour - hours.startHour) * 60;
  return ((minutes - hours.startHour * 60) / windowMinutes) * 100;
}

// Where an hour's line and its label sit — the same basis as a chip starting at
// that hour, which is the property the week grid's alignment rests on.
export function hourOffsetPercent(hour: number, hours: HourRange): number {
  return minuteOffsetPercent(hour * 60, hours);
}

// Percentage offsets for positioning a chip inside the week grid's own column.
export function hourSpanStylePercent(
  entry: DayEntry,
  hours: HourRange,
): { top: string; height: string } {
  const windowMinutes = (hours.endHour - hours.startHour) * 60;
  const length = entry.endMinutes - entry.startMinutes;

  return {
    top: `${minuteOffsetPercent(entry.startMinutes, hours)}%`,
    height: `${(length / windowMinutes) * 100}%`,
  };
}

// ---------------------------------------------------------------------------
// Overlapping bookings in the week view
// ---------------------------------------------------------------------------

// A day's entry plus where it sits horizontally once overlaps are resolved.
//
// Without this every chip spanned the full column, so two bookings at the same
// time were drawn on top of one another and whichever came last in the DOM
// simply hid the other. A member with two overlapping bookings is ordinary —
// different resources, or a pooled one — so "there is only ever one thing at a
// time" was never a safe assumption.
export interface PositionedEntry {
  entry: DayEntry;
  // 0-based, and `columnCount` is the width of the *cluster* this entry belongs
  // to rather than the day's busiest moment: a crowded morning must not make
  // the afternoon's single booking a narrow sliver.
  column: number;
  columnCount: number;
}

// Interval-graph column packing. Entries are swept in start order and grouped
// into clusters of transitively-overlapping bookings; within a cluster each
// entry takes the first column whose previous occupant has already ended.
export function layOutDay(entries: readonly DayEntry[]): PositionedEntry[] {
  const sorted = [...entries].sort(
    (a, b) => a.startMinutes - b.startMinutes || b.endMinutes - a.endMinutes,
  );

  const positioned: PositionedEntry[] = [];
  let cluster: PositionedEntry[] = [];
  let columnEnds: number[] = [];

  const flush = () => {
    for (const member of cluster) {
      member.columnCount = columnEnds.length;
    }
    positioned.push(...cluster);
    cluster = [];
    columnEnds = [];
  };

  for (const entry of sorted) {
    // A new cluster starts as soon as an entry begins at or after everything
    // before it has ended — nothing here can overlap anything there.
    if (cluster.length > 0 && entry.startMinutes >= Math.max(...columnEnds)) {
      flush();
    }

    let column = columnEnds.findIndex((end) => end <= entry.startMinutes);
    if (column === -1) {
      column = columnEnds.length;
    }

    columnEnds[column] = entry.endMinutes;
    cluster.push({ entry, column, columnCount: 0 });
  }

  flush();
  return positioned;
}

// How short a booking has to be before its chip cannot stack a time line above
// a label line.
//
// A week row is a fixed 56px per hour, so this is a duration rather than a
// pixel measurement: two lines of 0.78rem text plus padding and border need
// about 40px, which is roughly 43 minutes. Below that the chip lays its time
// and label out on one line instead of clipping the label away — which is what
// it used to do, `overflow: hidden` silently swallowing the booking's name.
export const COMPACT_CHIP_MINUTES = 45;

export function isCompactChip(entry: DayEntry): boolean {
  return entry.endMinutes - entry.startMinutes < COMPACT_CHIP_MINUTES;
}
