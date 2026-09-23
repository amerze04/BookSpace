import { utcToResourceLocal } from '../../availability/date/local-date';

// Admin console phase 6. The blackout form's rules, as pure functions.
//
// **A blackout is an *instant*, unlike an availability window.** A window is a
// recurring weekly rule in the resource's wall clock (decision `0003`); a
// blackout is `StartsAtUtc`/`EndsAtUtc`, two real points in time. So this file
// has a job the windows editor did not: converting between what an admin types
// and what goes on the wire.
//
// **The admin types in the resource's timezone**, not their own. An admin in
// Warsaw blacking out a New York room means the room's Tuesday, not theirs —
// the same reasoning decision `0003` applies to availability, and the exact
// confusion the WP-7 click-through surfaced when a 10:00 resource-local booking
// rendered as 16:00 for the viewer. The form shows the viewer's own equivalent
// alongside, so the conversion is visible rather than assumed.

// "2026-09-24T14:30" — the value an <input type="datetime-local"> holds. Local
// wall clock with no zone, which is precisely why it has to be paired with one.
export type LocalDateTimeString = string;

// A wall-clock time in `timeZoneId` -> the UTC instant it names.
//
// **Two passes, correcting against `utcToResourceLocal`** — the one
// already-correct UTC->local conversion this app has, which defers entirely to
// the ICU/IANA database via `Intl.DateTimeFormat`. The same technique
// `availability-grid.ts` uses for its own selection conversion, generalized
// here to an arbitrary date rather than a day-segment, and for the same reason:
// hand-rolling a second, independent local->UTC engine is how the two drift.
//
// A first guess that treats the local digits as UTC is wrong by exactly the
// zone's offset; measuring that offset by converting the guess *back* and
// correcting gives the answer, and a second pass absorbs the case where the
// correction itself crossed a DST transition.
//
// **A local time that never happened (spring-forward's gap) or happened twice
// (fall-back) has no unique inverse.** For a gap this converges to whichever
// side the passes settle on rather than throwing; for an ambiguity it lands on
// the earlier candidate, which happens to match decision `0024`'s own policy
// for recurrence. Neither needs to be exact here — the server stores whatever
// instant it is sent, and a blackout an hour wide of a transition boundary is a
// far smaller problem than a form that refuses to submit.
export function resourceLocalToUtc(local: LocalDateTimeString, timeZoneId: string): string {
  const parsed = parseLocalDateTime(local);
  if (parsed === null) {
    throw new Error(`Not a datetime-local value: "${local}".`);
  }

  // Pass 0: pretend the digits are UTC.
  let guess = Date.UTC(parsed.year, parsed.month - 1, parsed.day, parsed.hour, parsed.minute, 0);

  for (let pass = 0; pass < 2; pass++) {
    const seenAs = utcToResourceLocal(new Date(guess).toISOString(), timeZoneId);
    const seenMinutes = minutesSinceEpochOfLocal(seenAs.date, seenAs.minutesOfDay);
    const wantedMinutes = minutesSinceEpochOfLocal(
      `${pad(parsed.year, 4)}-${pad(parsed.month, 2)}-${pad(parsed.day, 2)}`,
      parsed.hour * 60 + parsed.minute,
    );

    const driftMinutes = wantedMinutes - seenMinutes;
    if (driftMinutes === 0) {
      break;
    }

    guess += driftMinutes * 60_000;
  }

  return new Date(guess).toISOString().replace(/\.\d{3}Z$/, 'Z');
}

// The inverse, for seeding the form from a stored blackout.
export function utcToResourceLocalInput(utcIso: string, timeZoneId: string): LocalDateTimeString {
  const { date, minutesOfDay } = utcToResourceLocal(utcIso, timeZoneId);
  const hour = Math.floor(minutesOfDay / 60);
  const minute = minutesOfDay % 60;

  return `${date}T${pad(hour, 2)}:${pad(minute, 2)}`;
}

export interface BlackoutFormValue {
  startsAt: LocalDateTimeString;
  endsAt: LocalDateTimeString;
  reason: string;
}

export interface BlackoutFormErrors {
  startsAt?: string;
  endsAt?: string;
  reason?: string;
}

// Matches BlackoutPeriods.Reason NVARCHAR(300).
export const MAX_REASON_LENGTH = 300;

// The client-side rules, each mirroring one the server enforces, so a whole
// form is not refused for something a message could have said in place.
//
// **What is deliberately NOT checked here: whether the interval has already
// elapsed.** `BlackoutPeriodElapsed` is about `EndsAtUtc` against the *server's*
// clock, and a browser clock that disagrees would either refuse a legal blackout
// or promise one the server then rejects. The dialect handles the refusal
// instead. A blackout that *starts* in the past is legal and sometimes correct —
// a room that flooded this morning.
export function validateBlackout(value: BlackoutFormValue, timeZoneId: string): BlackoutFormErrors {
  const errors: BlackoutFormErrors = {};

  if (!value.startsAt) {
    errors.startsAt = 'Set a start.';
  }

  if (!value.endsAt) {
    errors.endsAt = 'Set an end.';
  }

  if (value.reason.length > MAX_REASON_LENGTH) {
    errors.reason = `A reason can be at most ${MAX_REASON_LENGTH} characters.`;
  }

  // CK_BlackoutPeriods_Interval, restated. Compared as instants rather than as
  // strings: the two ends can sit on opposite sides of a DST transition, where
  // the local digits and the real elapsed time disagree.
  if (!errors.startsAt && !errors.endsAt) {
    const startUtc = resourceLocalToUtc(value.startsAt, timeZoneId);
    const endUtc = resourceLocalToUtc(value.endsAt, timeZoneId);

    if (new Date(endUtc).getTime() <= new Date(startUtc).getTime()) {
      errors.endsAt = 'The end has to be after the start.';
    }
  }

  return errors;
}

export function hasErrors(errors: BlackoutFormErrors): boolean {
  return Object.keys(errors).length > 0;
}

function parseLocalDateTime(
  value: LocalDateTimeString,
): { year: number; month: number; day: number; hour: number; minute: number } | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  if (match === null) {
    return null;
  }

  return {
    year: Number(match[1]),
    month: Number(match[2]),
    day: Number(match[3]),
    hour: Number(match[4]),
    minute: Number(match[5]),
  };
}

// A local date plus minutes-of-day as one comparable number. Computed through
// Date.UTC purely as calendar arithmetic — no zone is implied, and the result is
// only ever subtracted from another produced the same way.
function minutesSinceEpochOfLocal(date: string, minutesOfDay: number): number {
  const [year, month, day] = date.split('-').map(Number);
  return Date.UTC(year, month - 1, day) / 60_000 + minutesOfDay;
}

function pad(value: number, length: number): string {
  return String(value).padStart(length, '0');
}
