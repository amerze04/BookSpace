import { AvailabilityWindowDetail, DayOfWeekName } from '../../resources/models/resources.models';

// Admin console phase 4. The weekly availability editor's rules, as pure
// functions — kept out of the component for the same reason
// `recurrence-form.ts` is: the interesting part is arithmetic over times, and
// arithmetic is worth testing without a TestBed.

// **Monday first.** The wire uses .NET's `DayOfWeek`, where Sunday is 0, and
// this app has picked Monday everywhere a person reads a week
// (`calendar-range.ts`, `recurrence-form.ts`). The order here is for display
// only; the weekday travels as its name, so nothing depends on the index.
export const EDITOR_WEEKDAYS: readonly DayOfWeekName[] = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
];

// **Decision `0022`: `ClosesAt = 23:59:59` unconditionally means the following
// midnight.** Not 23:59:59 — midnight. It is the only way to express "open
// until the end of the day" in a `time` column, and it is a convention rather
// than a value, which is exactly why it needs a name instead of appearing as a
// magic string in a template.
export const END_OF_DAY = '23:59:59';

// What the editor holds for one row. `closesAtEndOfDay` is a separate flag
// rather than a `closesAt` of "23:59:59", so the editor never has to guess
// whether an admin meant the convention or meant one second to midnight — the
// two are indistinguishable on the wire and must not be on screen.
export interface WindowRow {
  // Local to this editing session only; the server mints real ids on save and
  // a replace-the-set endpoint gives a client nothing to correlate them to.
  key: string;
  weekday: DayOfWeekName;
  opensAt: string;
  closesAt: string;
  closesAtEndOfDay: boolean;
}

export interface WindowRowError {
  key: string;
  message: string;
}

let nextKey = 0;

export function newRowKey(): string {
  nextKey += 1;
  return `w${nextKey}`;
}

// "09:00:00" (the wire) -> "09:00" (an <input type="time"> value). Seconds are
// dropped because the validator refuses anything but whole seconds and the
// editor offers only whole minutes; a stored ":30" would be a value this
// editor could not have produced.
export function toInputTime(wireTime: string): string {
  return wireTime.slice(0, 5);
}

// "09:00" -> "09:00:00". The inverse, and the only place the wire's seconds
// are invented.
export function toWireTime(inputTime: string): string {
  return `${inputTime}:00`;
}

// The server's schedule as editable rows, in the order a person reads a week.
export function toRows(windows: readonly AvailabilityWindowDetail[]): WindowRow[] {
  return [...windows]
    .sort(compareByWeekdayThenOpens)
    .map((window) => ({
      key: newRowKey(),
      weekday: window.weekday,
      opensAt: toInputTime(window.opensAt),
      // A stored 23:59:59 is the convention, so it comes back as the flag
      // rather than as a time nobody typed.
      closesAt: window.closesAt === END_OF_DAY ? '' : toInputTime(window.closesAt),
      closesAtEndOfDay: window.closesAt === END_OF_DAY,
    }));
}

// Rows as the endpoint wants them. Order is not significant to the server — it
// replaces the set and returns its own ordering — but sorting keeps a request
// readable in a log next to the screen that produced it.
export function toWindowPayload(rows: readonly WindowRow[]): Array<{
  weekday: DayOfWeekName;
  opensAt: string;
  closesAt: string;
}> {
  return [...rows]
    .sort(compareRows)
    .map((row) => ({
      weekday: row.weekday,
      opensAt: toWireTime(row.opensAt),
      closesAt: row.closesAtEndOfDay ? END_OF_DAY : toWireTime(row.closesAt),
    }));
}

// Minutes since midnight, with decision `0022` applied: a row closing at end of
// day reads as 1440 rather than 1439, or every such window would end one minute
// short of the day it is meant to cover. Same reading `availability-grid.ts`
// already applies to the stored value.
export const MINUTES_PER_DAY = 24 * 60;

export function opensAtMinutes(row: WindowRow): number {
  return toMinutes(row.opensAt);
}

export function closesAtMinutes(row: WindowRow): number {
  return row.closesAtEndOfDay ? MINUTES_PER_DAY : toMinutes(row.closesAt);
}

// Every reason a row cannot be saved, keyed by row. Refused here rather than at
// the server, which is what `docs/admin-plan.md` §4 asks for — not because the
// server would not catch it (it would: `ClosesAt > OpensAt` is a 400 and an
// overlap is a 409) but because a whole weekly schedule refused as one request
// tells an admin nothing about which of fourteen rows was wrong.
//
// **The overlap rule is the server's, restated exactly**, including the part
// that looks like a bug: adjacency is not overlap. `ClosesAt` is exclusive, so
// 09:00-12:00 and 12:00-17:00 coexist, and refusing that pair here would make
// this editor stricter than the API it writes to.
export function validateRows(rows: readonly WindowRow[]): WindowRowError[] {
  const errors: WindowRowError[] = [];

  for (const row of rows) {
    if (!row.opensAt) {
      errors.push({ key: row.key, message: 'Set an opening time.' });
      continue;
    }

    if (!row.closesAtEndOfDay && !row.closesAt) {
      errors.push({ key: row.key, message: 'Set a closing time, or tick "until end of day".' });
      continue;
    }

    if (closesAtMinutes(row) <= opensAtMinutes(row)) {
      errors.push({
        key: row.key,
        message: 'The closing time has to be after the opening time.',
      });
    }
  }

  // Overlaps are checked only among rows that are otherwise sound: reporting
  // "this overlaps" about a row whose own times are nonsense is noise on top of
  // the real problem.
  const sound = rows.filter((row) => !errors.some((e) => e.key === row.key));

  for (const weekday of EDITOR_WEEKDAYS) {
    const ordered = sound
      .filter((row) => row.weekday === weekday)
      .sort((a, b) => opensAtMinutes(a) - opensAtMinutes(b));

    for (let i = 1; i < ordered.length; i++) {
      if (opensAtMinutes(ordered[i]) < closesAtMinutes(ordered[i - 1])) {
        errors.push({
          key: ordered[i].key,
          message: `This overlaps another ${weekday} window. Windows on a day cannot overlap.`,
        });
      }
    }
  }

  return errors;
}

// Whether the editor holds anything different from what the server last sent.
// Compared over the payload shape rather than the rows, so a row that was
// deleted and re-added identically does not count as a change — the endpoint
// replaces the set, and an identical set is not an edit.
export function hasChanges(
  rows: readonly WindowRow[],
  original: readonly AvailabilityWindowDetail[],
): boolean {
  const current = JSON.stringify(toWindowPayload(rows));
  const saved = JSON.stringify(
    [...original].sort(compareByWeekdayThenOpens).map((w) => ({
      weekday: w.weekday,
      opensAt: w.opensAt,
      closesAt: w.closesAt,
    })),
  );

  return current !== saved;
}

function toMinutes(inputTime: string): number {
  const [hours, minutes] = inputTime.split(':').map(Number);
  return hours * 60 + minutes;
}

function weekdayIndex(weekday: DayOfWeekName): number {
  return EDITOR_WEEKDAYS.indexOf(weekday);
}

function compareRows(a: WindowRow, b: WindowRow): number {
  return weekdayIndex(a.weekday) - weekdayIndex(b.weekday) || opensAtMinutes(a) - opensAtMinutes(b);
}

function compareByWeekdayThenOpens(
  a: AvailabilityWindowDetail,
  b: AvailabilityWindowDetail,
): number {
  return weekdayIndex(a.weekday) - weekdayIndex(b.weekday) || a.opensAt.localeCompare(b.opensAt);
}
