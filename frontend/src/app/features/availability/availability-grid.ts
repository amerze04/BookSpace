import { BookableInterval, LocalDateString } from './availability.models';
import { addDays, addMinutesToUtc, localDateDiffDays, utcToResourceLocal, weekdayOf } from './local-date';

// Grid-specific rendering math for step 5 — separate from local-date.ts's
// generic calendar arithmetic because everything here is about turning a
// BookableIntervalDetail (a UTC instant span) into something a day-row grid
// can actually draw, which local-date.ts's own callers (the range picker)
// have no need for.

// One bookable span clipped to a single resource-local calendar day.
// startMinutes/endMinutes are minutes since that day's local midnight, so
// endMinutes can be 1440 (a span running to the following midnight) but
// never more — the whole reason this type exists is that an interval
// spanning a local midnight is split into one DaySegment per day it touches,
// rather than the grid trying to draw a bar that crosses a day-row boundary.
//
// startUtc/endUtc (step 6) are this segment's own boundaries as real UTC
// instants — what "Continue to booking" needs once a viewer narrows their
// selection with the Start/End time dropdowns, since only the *original*
// BookableIntervalDetail carries a UTC instant otherwise. Derived from the
// interval's own startUtc by walking forward the number of *local* wall-clock
// minutes elapsed (addMinutesToUtc), not by re-deriving from local time —
// exact whenever no DST transition falls between the interval's start and
// this segment's own boundary (true for every resource in this codebase's
// seed data, and for any ordinary business-hours resource), and off by the
// DST delta in the rarer case of an overnight-spanning window whose midnight
// crossing lands on a transition night. Unlike the *selection* conversion
// below (resourceLocalMinutesToUtc), fixing this one would mean re-deriving
// every segment boundary independently rather than walking forward from a
// known-good one, which is a bigger change for a narrower edge case; the
// booking is re-validated against real availability under lock at submission
// time regardless (§4.1), so a rare wrong boundary here is a UX rough edge,
// never a double-booking risk.
export interface DaySegment {
  date: LocalDateString;
  startMinutes: number;
  endMinutes: number;
  startUtc: string;
  endUtc: string;
  remainingCapacity: number;
}

// Why a span inside the resource's opening hours is *not* bookable. The
// availability endpoint answers only with what **is** bookable (decision
// `0020`), so these are derived client-side by subtracting: opening hours
// minus bookable time is unbookable time, and a blackout the member can
// already read (GET /resources/{id}/blackout-periods is on TenantMember
// precisely so "a member choosing when to book can see when a resource is
// blacked out") is what tells the two apart.
//
//   'blackout' -> covered by a blackout period: "Unavailable"
//   'booked'   -> inside opening hours, not blacked out, yet not offered:
//                 something already holds it. "Booked"
//
// The second is deliberately the fallback rather than a separate positive
// test, because it is the only remaining explanation: a pooled resource with
// some — but fewer than the requested `quantity` — units left reads as
// "Booked" too, which is the honest answer to "why can't I take this?".
export type UnbookableKind = 'blackout' | 'booked';

export interface DayUnbookableSpan {
  startMinutes: number;
  endMinutes: number;
  kind: UnbookableKind;
}

export interface DayRow {
  date: LocalDateString;
  segments: DaySegment[];
  unbookable: DayUnbookableSpan[];
}

// A plain minute range on one local day — the shape the set arithmetic below
// works in, shared by opening windows, bookable segments and blackouts alike.
export interface MinuteSpan {
  startMinutes: number;
  endMinutes: number;
}

// The weekday + opening times of one AvailabilityWindowDetail, structurally
// rather than by importing the resources feature's own DTO: this module has
// stayed free of both Angular and the wire types, and one interface is a
// cheaper way to keep it that way than a cross-feature import.
export interface OpeningWindow {
  weekday: string;
  opensAt: string;
  closesAt: string;
}

export interface UtcSpan {
  startUtc: string;
  endUtc: string;
}

const MINUTES_PER_DAY = 24 * 60;

// Splits one interval into per-local-day segments. Same-day is the common
// case (a business-hours resource) and returns exactly one segment; a
// resource open across local midnight (decision `0022`'s "ClosesAt =
// 23:59:59 means the following midnight" can chain into the next day's own
// opening window, with nothing in the capacity sweep to force a cut at the
// boundary) returns one segment per day it touches, each clipped to that
// day's own [0, 1440) range.
export function splitIntervalByLocalDay(interval: BookableInterval, timeZoneId: string): DaySegment[] {
  const start = utcToResourceLocal(interval.startUtc, timeZoneId);
  const end = utcToResourceLocal(interval.endUtc, timeZoneId);

  // addMinutesToUtc(x, 0) rather than passing interval.startUtc/endUtc
  // through as-is — purely to normalize to one canonical ISO format
  // (via Date.toISOString()) regardless of how the backend happened to
  // serialize it, so every DaySegment's startUtc/endUtc looks the same
  // whether it came straight from the interval or was computed below.
  if (start.date === end.date) {
    return [
      {
        date: start.date,
        startMinutes: start.minutesOfDay,
        endMinutes: end.minutesOfDay,
        startUtc: addMinutesToUtc(interval.startUtc, 0),
        endUtc: addMinutesToUtc(interval.endUtc, 0),
        remainingCapacity: interval.remainingCapacity,
      },
    ];
  }

  // Walks forward from interval.startUtc by local wall-clock minutes elapsed
  // so far, rather than converting each boundary's local time back to UTC
  // independently — see DaySegment's own comment for what that trades away.
  let minutesFromIntervalStart = MINUTES_PER_DAY - start.minutesOfDay;
  const segments: DaySegment[] = [
    {
      date: start.date,
      startMinutes: start.minutesOfDay,
      endMinutes: MINUTES_PER_DAY,
      startUtc: addMinutesToUtc(interval.startUtc, 0),
      endUtc: addMinutesToUtc(interval.startUtc, minutesFromIntervalStart),
      remainingCapacity: interval.remainingCapacity,
    },
  ];

  let cursor = addDays(start.date, 1);
  while (cursor < end.date) {
    const segmentStartUtc = addMinutesToUtc(interval.startUtc, minutesFromIntervalStart);
    minutesFromIntervalStart += MINUTES_PER_DAY;
    segments.push({
      date: cursor,
      startMinutes: 0,
      endMinutes: MINUTES_PER_DAY,
      startUtc: segmentStartUtc,
      endUtc: addMinutesToUtc(interval.startUtc, minutesFromIntervalStart),
      remainingCapacity: interval.remainingCapacity,
    });
    cursor = addDays(cursor, 1);
  }

  // end.minutesOfDay === 0 means the interval stops exactly at end.date's own
  // midnight — nothing of it actually falls on that day, so it gets no
  // segment (the loop above already stopped one day short of it).
  if (end.minutesOfDay > 0) {
    segments.push({
      date: end.date,
      startMinutes: 0,
      endMinutes: end.minutesOfDay,
      startUtc: addMinutesToUtc(interval.startUtc, minutesFromIntervalStart),
      endUtc: addMinutesToUtc(interval.endUtc, 0),
      remainingCapacity: interval.remainingCapacity,
    });
  }

  return segments;
}

// Every date from `from` to `to`, inclusive — the grid's own day rows,
// independent of which days actually have a bookable segment (a closed day
// still gets its own "No bookable hours" row, per the design).
export function datesInRange(from: LocalDateString, to: LocalDateString): LocalDateString[] {
  const dates: LocalDateString[] = [];
  let cursor = from;
  while (cursor <= to) {
    dates.push(cursor);
    cursor = addDays(cursor, 1);
  }
  return dates;
}

// `windows` and `blackouts` are optional because they only feed the
// *unbookable* half of a row: without them a row still renders exactly as it
// did before, with its bookable bars and nothing between them.
export function buildDayRows(
  dates: readonly LocalDateString[],
  intervals: readonly BookableInterval[],
  timeZoneId: string,
  windows: readonly OpeningWindow[] = [],
  blackouts: readonly UtcSpan[] = [],
): DayRow[] {
  const segmentsByDate = new Map<LocalDateString, DaySegment[]>();

  for (const interval of intervals) {
    for (const segment of splitIntervalByLocalDay(interval, timeZoneId)) {
      const existing = segmentsByDate.get(segment.date);
      if (existing) {
        existing.push(segment);
      } else {
        segmentsByDate.set(segment.date, [segment]);
      }
    }
  }

  // Blackouts are UTC instants like bookable intervals, and can span a local
  // midnight for the same reasons — so they go through the same splitter
  // rather than a second, subtly different conversion. remainingCapacity is
  // irrelevant here and only present to satisfy the shared shape.
  const blackoutsByDate = new Map<LocalDateString, MinuteSpan[]>();
  for (const blackout of blackouts) {
    for (const segment of splitIntervalByLocalDay({ ...blackout, remainingCapacity: 0 }, timeZoneId)) {
      const existing = blackoutsByDate.get(segment.date);
      const span = { startMinutes: segment.startMinutes, endMinutes: segment.endMinutes };
      if (existing) {
        existing.push(span);
      } else {
        blackoutsByDate.set(segment.date, [span]);
      }
    }
  }

  return dates.map((date) => {
    const segments = (segmentsByDate.get(date) ?? []).sort((a, b) => a.startMinutes - b.startMinutes);
    return {
      date,
      segments,
      unbookable: buildUnbookableSpans(date, segments, windows, blackoutsByDate.get(date) ?? []),
    };
  });
}

// Opening hours minus bookable time, with whatever a blackout covers labelled
// as such. Returns nothing at all when the resource has no window for this
// weekday: a closed day is not "unavailable", it is simply not open, which the
// row's own empty-day label already says.
function buildUnbookableSpans(
  date: LocalDateString,
  segments: readonly MinuteSpan[],
  windows: readonly OpeningWindow[],
  blackouts: readonly MinuteSpan[],
): DayUnbookableSpan[] {
  const openSpans = openSpansFor(date, windows);
  if (openSpans.length === 0) {
    return [];
  }

  const spans: DayUnbookableSpan[] = [];
  for (const gap of subtractSpans(openSpans, segments)) {
    for (const covered of intersectSpans([gap], blackouts)) {
      spans.push({ ...covered, kind: 'blackout' });
    }
    for (const free of subtractSpans([gap], blackouts)) {
      spans.push({ ...free, kind: 'booked' });
    }
  }

  // One pill per continuous reason, never one per boundary that happened to
  // fall inside it (owner's correction, 2026-09-17). Two sources can cut a
  // single stretch in half without any change in *why* it is unbookable: two
  // touching opening windows — which the seeded 3D Printer really has,
  // 09:00-12:00 and 12:00-17:00, so one 11:00-13:00 blackout rendered as
  // "Unavailable 11-12" and "Unavailable 12-13" — and two abutting blackout
  // rows. mergeSpans on the windows above handles the first; merging the
  // result by kind here handles the second and anything else.
  return mergeAdjacentByKind(spans.sort((a, b) => a.startMinutes - b.startMinutes));
}

function mergeAdjacentByKind(spans: readonly DayUnbookableSpan[]): DayUnbookableSpan[] {
  const merged: DayUnbookableSpan[] = [];

  for (const span of spans) {
    const previous = merged[merged.length - 1];
    if (previous && previous.kind === span.kind && span.startMinutes <= previous.endMinutes) {
      previous.endMinutes = Math.max(previous.endMinutes, span.endMinutes);
      continue;
    }
    merged.push({ ...span });
  }

  return merged;
}

// The resource's own opening hours for one calendar date, in local minutes.
//
// Windows that touch or overlap are merged into one span; windows with a real
// gap between them (a split shift — closed over lunch, say) are not. The
// distinction matters in both directions: merging across a genuine gap would
// label closed time as unbookable, while *not* merging two contiguous windows
// splits a single continuous stretch at a boundary that means nothing to a
// member — which is exactly what the seeded 3D Printer's own 09:00-12:00 +
// 12:00-17:00 pair did to one 11:00-13:00 blackout.
function openSpansFor(date: LocalDateString, windows: readonly OpeningWindow[]): MinuteSpan[] {
  return openSpansForWeekday(weekdayOf(date), windows);
}

// The same thing for a weekday name rather than a date — what the recurring
// booking form needs, since a series' occurrences are a weekday and a time
// rather than a list of dates (`recurrence-form.ts`).
export function openSpansForWeekday(
  weekday: string,
  windows: readonly OpeningWindow[],
): MinuteSpan[] {
  const spans = windows
    .filter((window) => window.weekday === weekday)
    .map((window) => ({
      startMinutes: parseLocalTimeToMinutes(window.opensAt),
      endMinutes: parseLocalTimeToMinutes(window.closesAt),
    }))
    .filter((span) => span.endMinutes > span.startMinutes)
    .sort((a, b) => a.startMinutes - b.startMinutes);

  return mergeSpans(spans);
}

// Touching or overlapping spans become one; a real gap keeps them apart.
function mergeSpans(spans: readonly MinuteSpan[]): MinuteSpan[] {
  const merged: MinuteSpan[] = [];

  for (const span of [...spans].sort((a, b) => a.startMinutes - b.startMinutes)) {
    const previous = merged[merged.length - 1];
    if (previous && span.startMinutes <= previous.endMinutes) {
      previous.endMinutes = Math.max(previous.endMinutes, span.endMinutes);
      continue;
    }
    merged.push({ ...span });
  }

  return merged;
}

// "09:00:00" -> 540. Decision `0022`: a window closing at 23:59:59 means the
// *following midnight*, so it reads as 1440 rather than 1439 — otherwise the
// last minute of such a day would always render as an unbookable sliver.
function parseLocalTimeToMinutes(time: string): number {
  const [hours, minutes, seconds] = time.split(':').map(Number);
  if (hours === 23 && minutes === 59 && seconds === 59) {
    return MINUTES_PER_DAY;
  }
  return hours * 60 + minutes;
}

// `base` minus `cuts`, both as minute ranges on one day. Plain interval
// arithmetic, kept here beside its only callers rather than generalized.
function subtractSpans(base: readonly MinuteSpan[], cuts: readonly MinuteSpan[]): MinuteSpan[] {
  let remaining = base.map((span) => ({ ...span }));

  for (const cut of cuts) {
    const next: MinuteSpan[] = [];
    for (const span of remaining) {
      if (cut.endMinutes <= span.startMinutes || cut.startMinutes >= span.endMinutes) {
        next.push(span);
        continue;
      }
      if (cut.startMinutes > span.startMinutes) {
        next.push({ startMinutes: span.startMinutes, endMinutes: cut.startMinutes });
      }
      if (cut.endMinutes < span.endMinutes) {
        next.push({ startMinutes: cut.endMinutes, endMinutes: span.endMinutes });
      }
    }
    remaining = next;
  }

  return remaining.filter((span) => span.endMinutes > span.startMinutes);
}

function intersectSpans(left: readonly MinuteSpan[], right: readonly MinuteSpan[]): MinuteSpan[] {
  const result: MinuteSpan[] = [];
  for (const a of left) {
    for (const b of right) {
      const startMinutes = Math.max(a.startMinutes, b.startMinutes);
      const endMinutes = Math.min(a.endMinutes, b.endMinutes);
      if (endMinutes > startMinutes) {
        result.push({ startMinutes, endMinutes });
      }
    }
  }
  return result;
}

export interface GridAxis {
  startMinutes: number;
  endMinutes: number;
}

const DEFAULT_AXIS: GridAxis = { startMinutes: 8 * 60, endMinutes: 18 * 60 };

// The shared time axis every day row draws its bars against — one axis for
// the whole visible window, not one per row, so a bar's horizontal position
// means the same thing on every line. Padded out to whole hours (floor the
// earliest start, ceil the latest end) so the axis ticks land on round
// numbers instead of wherever the first booking happened to start.
export function computeAxis(dayRows: readonly DayRow[]): GridAxis {
  let min = Infinity;
  let max = -Infinity;

  for (const row of dayRows) {
    for (const segment of row.segments) {
      min = Math.min(min, segment.startMinutes);
      max = Math.max(max, segment.endMinutes);
    }
    // Unbookable spans count too, or a day whose whole morning is blacked out
    // would have that bar clamped against an axis that starts after it — and
    // a day that is *entirely* blacked out would push the axis nowhere at all
    // while still needing somewhere to draw.
    for (const span of row.unbookable) {
      min = Math.min(min, span.startMinutes);
      max = Math.max(max, span.endMinutes);
    }
  }

  if (!Number.isFinite(min) || !Number.isFinite(max)) {
    return DEFAULT_AXIS;
  }

  const startMinutes = Math.floor(min / 60) * 60;
  const endMinutes = Math.ceil(max / 60) * 60;
  return { startMinutes, endMinutes: Math.max(endMinutes, startMinutes + 60) };
}

// Tick marks every 2 hours across the axis, matching the design's own
// 08:00/10:00/12:00/.../18:00 header — starting from the first even hour at
// or after the axis start.
export function buildAxisTicks(axis: GridAxis): number[] {
  const ticks: number[] = [];
  const firstTick = Math.ceil(axis.startMinutes / 120) * 120;
  for (let minute = firstTick; minute <= axis.endMinutes; minute += 120) {
    ticks.push(minute);
  }
  return ticks;
}

// A span's horizontal position/width as a percentage of the axis, for a CSS
// `left`/`width` style — clamped so a span that (in principle) starts or
// ends outside the shared axis still renders inside the row rather than
// overflowing it. Takes just the two minute fields, not a full DaySegment,
// so step 6's selection overlay (a narrowed sub-range with no date/UTC/
// capacity of its own) can reuse the exact same positioning math a bar does.
export function minuteSpanStylePercent(
  span: { startMinutes: number; endMinutes: number },
  axis: GridAxis,
): { leftPercent: number; widthPercent: number } {
  const totalSpan = axis.endMinutes - axis.startMinutes;
  const left = clamp(span.startMinutes, axis.startMinutes, axis.endMinutes);
  const right = clamp(span.endMinutes, axis.startMinutes, axis.endMinutes);
  return {
    leftPercent: ((left - axis.startMinutes) / totalSpan) * 100,
    widthPercent: Math.max(((right - left) / totalSpan) * 100, 0),
  };
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

// Inverts utcToResourceLocal: given a target resource-local minutes-of-day
// on `segment.date`, finds the real UTC instant that reads back as exactly
// that local time (step 6's "Continue to booking" conversion). Replaces a
// flat "add local minutes to segment.startUtc" offset, which is only exact
// when no DST transition falls between segment.startUtc and the target —
// across one, local and UTC minutes stop moving in lockstep by exactly the
// transition's own delta (not always 60 minutes — Australia/Lord_Howe's own
// transition is 30).
//
// Starts from that same flat-offset guess — correct the overwhelming
// majority of the time, since most days have no transition at all — and
// corrects it against utcToResourceLocal, the one already-correct
// UTC->local conversion this app has (it defers entirely to the ICU/IANA
// timezone database via Intl.DateTimeFormat), rather than hand-rolling a
// second, independent local->UTC engine. Two passes are enough for any
// single transition inside the segment: the first pass's correction can
// only be wrong by the transition's own fixed delta, and the second pass
// has nothing left to correct once that's accounted for.
//
// A local time that never happened (spring-forward's gap) or happened twice
// (fall-back's ambiguity) has no unique inverse. For a gap, this returns
// whatever the two passes converge closest to rather than throwing. For an
// ambiguous time, starting from segment.startUtc — always at or before the
// first occurrence, since a DaySegment never crosses its own local day —
// means the flat guess already lands in the *earlier* candidate's frame and
// needs no correction at all, which happens to match decisions/0024's own
// "earlier of the two" policy for exactly the same case server-side.
// Neither edge case needs to be exact: the backend re-validates every
// booking under lock at submission time regardless (§4.1), so a rare wrong
// pre-fill here is a UX rough edge a viewer can correct via the dropdowns,
// never a double-booking risk.
export function resourceLocalMinutesToUtc(segment: DaySegment, targetMinutesOfDay: number, timeZoneId: string): string {
  let candidate = addMinutesToUtc(segment.startUtc, targetMinutesOfDay - segment.startMinutes);

  for (let pass = 0; pass < 2; pass++) {
    const reading = utcToResourceLocal(candidate, timeZoneId);
    const readingMinutesOfDay = reading.minutesOfDay + localDateDiffDays(segment.date, reading.date) * MINUTES_PER_DAY;
    const errorMinutes = readingMinutesOfDay - targetMinutesOfDay;
    if (errorMinutes === 0) {
      break;
    }
    candidate = addMinutesToUtc(candidate, -errorMinutes);
  }

  return candidate;
}
