import { BookableInterval, LocalDateString } from './availability.models';
import { addDays, addMinutesToUtc, utcToResourceLocal } from './local-date';

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
// exact whenever no DST transition falls inside the interval (true for every
// resource in this codebase's seed data, and for any ordinary business-hours
// resource), and off by the DST delta in the one case it does. Accepted: the
// booking is re-validated against real availability under lock at submission
// time regardless (§4.1), so a rare wrong pre-fill here is a UX rough edge a
// viewer can correct via the dropdowns, never a double-booking risk.
export interface DaySegment {
  date: LocalDateString;
  startMinutes: number;
  endMinutes: number;
  startUtc: string;
  endUtc: string;
  remainingCapacity: number;
}

export interface DayRow {
  date: LocalDateString;
  segments: DaySegment[];
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

export function buildDayRows(
  dates: readonly LocalDateString[],
  intervals: readonly BookableInterval[],
  timeZoneId: string,
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

  return dates.map((date) => ({
    date,
    segments: (segmentsByDate.get(date) ?? []).sort((a, b) => a.startMinutes - b.startMinutes),
  }));
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
