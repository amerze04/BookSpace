import { BookingStatus, BookingSummary } from '../../booking/models/booking.models';
import {
  buildCalendarQueryParams,
  buildDayCells,
  chipsForCell,
  datesInRange,
  endOfMonth,
  fetchWindowUtc,
  hourOffsetPercent,
  hourSpanStylePercent,
  isCompactChip,
  isDrawnOnCalendar,
  layOutDay,
  localDateOf,
  localMinutesOf,
  minuteOffsetPercent,
  parseCalendarUrlState,
  startOfMonth,
  startOfWeek,
  stepRange,
  viewerToday,
  visibleRange,
  weekHourRange,
} from '../grid/calendar-range';

// **Every instant in this file is built from local components**
// (`new Date(2026, 8, 24, 13, 15)`), never from a UTC literal. The calendar
// reads instants in the viewer's own zone, so a hard-coded "...T13:15:00Z"
// would make these assertions silently depend on where they run — passing in
// UTC (GitHub Actions) and CET (where they are written) and failing elsewhere.
// Building from local parts means the expectations hold in any zone.
function localInstant(
  year: number,
  monthIndex: number,
  day: number,
  hours = 0,
  minutes = 0,
): string {
  return new Date(year, monthIndex, day, hours, minutes).toISOString();
}

function booking(overrides: Partial<BookingSummary> = {}): BookingSummary {
  return {
    id: 'b1',
    resourceId: 'r1',
    resourceName: 'Conference Room A',
    userId: 'u1',
    userName: 'Member One',
    recurrenceRuleId: null,
    startsAtUtc: localInstant(2026, 8, 24, 9, 0),
    endsAtUtc: localInstant(2026, 8, 24, 10, 0),
    quantity: 1,
    title: null,
    status: 'Confirmed',
    createdAtUtc: '2026-09-20T08:00:00Z',
    ...overrides,
  };
}

function params(values: Record<string, string>) {
  return { get: (name: string) => values[name] ?? null };
}

describe('the calendar URL contract', () => {
  it('defaults to the month view anchored on today', () => {
    expect(parseCalendarUrlState(params({}), '2026-09-18')).toEqual({
      view: 'month',
      date: '2026-09-18',
    });
  });

  it('reads an explicit view and date', () => {
    expect(parseCalendarUrlState(params({ view: 'week', date: '2026-09-21' }), '2026-09-18')).toEqual(
      { view: 'week', date: '2026-09-21' },
    );
  });

  // A calendar has no invalid state to report — every date is one somebody
  // could legitimately navigate to — so a typo is absorbed rather than raised.
  it.each([
    ['an unknown view', { view: 'agenda' }],
    ['a non-date', { date: 'tomorrow' }],
    ['a loose date the Date constructor would still accept', { date: '2026-9-1' }],
    ['an impossible month', { date: '2026-13-01' }],
    ['an impossible day for the month', { date: '2026-02-30' }],
  ])('falls back to today for %s', (_label, values) => {
    expect(parseCalendarUrlState(params(values), '2026-09-18')).toEqual({
      view: 'month',
      date: '2026-09-18',
    });
  });

  it('accepts a real leap day but not a fabricated one', () => {
    expect(parseCalendarUrlState(params({ date: '2028-02-29' }), '2026-09-18').date).toBe('2028-02-29');
    expect(parseCalendarUrlState(params({ date: '2026-02-29' }), '2026-09-18').date).toBe('2026-09-18');
  });

  it('round-trips through the query params it writes', () => {
    const state = { view: 'week', date: '2026-09-21' } as const;
    expect(parseCalendarUrlState(params(buildCalendarQueryParams(state)), '2026-09-18')).toEqual(state);
  });
});

describe('week and month boundaries', () => {
  // Monday-first, matching both designs and the resource detail screen's own
  // bookable-hours table rather than DayOfWeek's Sunday-first ordering.
  it('starts a week on Monday, including for a Sunday', () => {
    expect(startOfWeek('2026-09-23')).toBe('2026-09-21'); // Wednesday
    expect(startOfWeek('2026-09-21')).toBe('2026-09-21'); // Monday itself
    expect(startOfWeek('2026-09-27')).toBe('2026-09-21'); // Sunday belongs to the week it ends
  });

  it('finds the first and last day of a month, including February', () => {
    expect(startOfMonth('2026-09-18')).toBe('2026-09-01');
    expect(endOfMonth('2026-09-18')).toBe('2026-09-30');
    expect(endOfMonth('2026-02-10')).toBe('2026-02-28');
    expect(endOfMonth('2028-02-10')).toBe('2028-02-29');
  });

  // The exact range the provided month design shows for September 2026.
  it('covers a month with whole weeks, leading and trailing', () => {
    expect(visibleRange('month', '2026-09-18')).toEqual({ start: '2026-08-31', end: '2026-10-04' });
  });

  it('covers a week Monday to Sunday', () => {
    expect(visibleRange('week', '2026-09-23')).toEqual({ start: '2026-09-21', end: '2026-09-27' });
  });

  // As many weeks as the month needs, never padded to a fixed six.
  it('uses only the weeks a month actually touches', () => {
    // February 2027 starts on a Monday and has 28 days — exactly four weeks.
    expect(datesInRange(visibleRange('month', '2027-02-10'))).toHaveLength(28);
    expect(datesInRange(visibleRange('month', '2026-09-18'))).toHaveLength(35);
  });
});

describe('stepping', () => {
  it('steps a week by seven days', () => {
    expect(stepRange('week', '2026-09-21', 1)).toBe('2026-09-28');
    expect(stepRange('week', '2026-09-21', -1)).toBe('2026-09-14');
  });

  it('steps a month across a year boundary', () => {
    expect(stepRange('month', '2026-12-10', 1)).toBe('2027-01-01');
    expect(stepRange('month', '2026-01-10', -1)).toBe('2025-12-01');
  });

  // The anchoring matters: stepping from the 31st through a short month would
  // otherwise clamp to the 28th and then stay there, so March would come back
  // as the 28th rather than the month the member stepped into.
  it('does not drift when stepping from a day the next month does not have', () => {
    const february = stepRange('month', '2026-01-31', 1);
    expect(february).toBe('2026-02-01');
    expect(stepRange('month', february, 1)).toBe('2026-03-01');
  });
});

describe('the fetch window', () => {
  const window = fetchWindowUtc({ start: '2026-08-31', end: '2026-10-04' });

  // ListBookingsQueryRequestValidator refuses an Unspecified instant outright:
  // a filter window silently shifted by a guessed zone is a wrong answer with
  // no error.
  it('carries a zone designator on both ends', () => {
    expect(window.from).toMatch(/Z$/);
    expect(window.to).toMatch(/Z$/);
  });

  it('carries no fractional seconds', () => {
    expect(window.from).not.toMatch(/\.\d+Z$/);
    expect(window.to).not.toMatch(/\.\d+Z$/);
  });

  // Equal is refused server-side too, not just inverted.
  it('ends strictly after it starts', () => {
    expect(new Date(window.to).getTime()).toBeGreaterThan(new Date(window.from).getTime());
  });

  // The window is the visible range read in the viewer's own zone — so it opens
  // at the first visible day's local midnight and closes at local midnight
  // after the last, whatever offset the viewer happens to be at.
  it('opens and closes at viewer-local midnight', () => {
    expect(localDateOf(window.from)).toBe('2026-08-31');
    expect(localMinutesOf(window.from)).toBe(0);
    expect(localDateOf(window.to)).toBe('2026-10-05');
    expect(localMinutesOf(window.to)).toBe(0);
  });
});

describe('which statuses the calendar draws', () => {
  // Cancelled and Rejected hold no time, so neither has a cell to occupy; the
  // member is told by email either way (NotificationKind). Owner's call,
  // 2026-09-18.
  it.each<[BookingStatus, boolean]>([
    ['Confirmed', true],
    ['Pending', true],
    ['Completed', true],
    ['NoShow', true],
    ['Cancelled', false],
    ['Rejected', false],
  ])('draws %s: %s', (status, drawn) => {
    expect(isDrawnOnCalendar(status)).toBe(drawn);
  });

  it('drops undrawn bookings before they ever reach a cell', () => {
    const cells = buildDayCells(
      [
        booking({ id: 'keep', status: 'Confirmed' }),
        booking({ id: 'gone', status: 'Cancelled' }),
        booking({ id: 'also-gone', status: 'Rejected' }),
      ],
      { start: '2026-09-24', end: '2026-09-24' },
      '2026-09-01',
      '2026-09-18',
    );

    expect(cells[0].entries.map((e) => e.booking.id)).toEqual(['keep']);
  });
});

describe('laying bookings into day cells', () => {
  const range = { start: '2026-08-31', end: '2026-10-04' } as const;

  it('marks today and the days outside the anchored month', () => {
    const cells = buildDayCells([], range, '2026-09-01', '2026-09-18');

    expect(cells[0]).toMatchObject({ date: '2026-08-31', inAnchoredMonth: false, isToday: false });
    expect(cells.find((c) => c.date === '2026-09-18')).toMatchObject({
      inAnchoredMonth: true,
      isToday: true,
      dayOfMonth: 18,
    });
    expect(cells[cells.length - 1]).toMatchObject({ date: '2026-10-04', inAnchoredMonth: false });
  });

  it('places a booking on its own viewer-local day, with day-relative minutes', () => {
    const cells = buildDayCells(
      [booking({ startsAtUtc: localInstant(2026, 8, 24, 9, 30), endsAtUtc: localInstant(2026, 8, 24, 11, 0) })],
      range,
      '2026-09-01',
      '2026-09-18',
    );

    const cell = cells.find((c) => c.date === '2026-09-24');
    expect(cell?.entries).toHaveLength(1);
    expect(cell?.entries[0]).toMatchObject({ startMinutes: 570, endMinutes: 660 });
  });

  // An overnight booking is one row on the API and two cells on a calendar —
  // clipped at midnight on each side, and flagged so a chip can say it
  // continues rather than claiming the booking ends at midnight.
  it('splits a booking that crosses local midnight across both days', () => {
    const cells = buildDayCells(
      [booking({ startsAtUtc: localInstant(2026, 8, 24, 22, 0), endsAtUtc: localInstant(2026, 8, 25, 2, 0) })],
      range,
      '2026-09-01',
      '2026-09-18',
    );

    expect(cells.find((c) => c.date === '2026-09-24')?.entries[0]).toMatchObject({
      startMinutes: 22 * 60,
      endMinutes: 24 * 60,
      continuesFromPreviousDay: false,
      continuesIntoNextDay: true,
    });
    expect(cells.find((c) => c.date === '2026-09-25')?.entries[0]).toMatchObject({
      startMinutes: 0,
      endMinutes: 120,
      continuesFromPreviousDay: true,
      continuesIntoNextDay: false,
    });
  });

  // The boundary case the split above would otherwise get wrong: a booking that
  // ends exactly at midnight belongs to the day that just closed, not to a
  // zero-length sliver of the one opening.
  it('keeps a booking ending exactly at midnight off the next day', () => {
    const cells = buildDayCells(
      [booking({ startsAtUtc: localInstant(2026, 8, 24, 22, 0), endsAtUtc: localInstant(2026, 8, 25, 0, 0) })],
      range,
      '2026-09-01',
      '2026-09-18',
    );

    expect(cells.find((c) => c.date === '2026-09-24')?.entries).toHaveLength(1);
    expect(cells.find((c) => c.date === '2026-09-24')?.entries[0].endMinutes).toBe(24 * 60);
    expect(cells.find((c) => c.date === '2026-09-25')?.entries).toHaveLength(0);
  });

  it('orders a day by start time, longest first on a tie', () => {
    const cells = buildDayCells(
      [
        booking({ id: 'later', startsAtUtc: localInstant(2026, 8, 24, 14, 0), endsAtUtc: localInstant(2026, 8, 24, 15, 0) }),
        booking({ id: 'short', startsAtUtc: localInstant(2026, 8, 24, 9, 0), endsAtUtc: localInstant(2026, 8, 24, 10, 0) }),
        booking({ id: 'long', startsAtUtc: localInstant(2026, 8, 24, 9, 0), endsAtUtc: localInstant(2026, 8, 24, 12, 0) }),
      ],
      range,
      '2026-09-01',
      '2026-09-18',
    );

    expect(cells.find((c) => c.date === '2026-09-24')?.entries.map((e) => e.booking.id)).toEqual([
      'long',
      'short',
      'later',
    ]);
  });
});

describe('capping the chips a month cell draws', () => {
  // Quarter-hour steps from 08:00, so even forty of them stay inside the one
  // day being capped — an hourly step would roll past midnight and land on the
  // next, quietly testing something smaller than it claimed.
  const entries = (count: number) =>
    buildDayCells(
      Array.from({ length: count }, (_, i) =>
        booking({
          id: `b${i}`,
          startsAtUtc: localInstant(2026, 8, 24, 8 + Math.floor(i / 4), (i % 4) * 15),
          endsAtUtc: localInstant(2026, 8, 24, 8 + Math.floor(i / 4), (i % 4) * 15 + 10),
        }),
      ),
      { start: '2026-09-24', end: '2026-09-24' },
      '2026-09-01',
      '2026-09-18',
    )[0].entries;

  it('shows everything when the day fits', () => {
    expect(chipsForCell(entries(3), false, 3)).toMatchObject({ hiddenCount: 0 });
    expect(chipsForCell(entries(3), false, 3).shown).toHaveLength(3);
  });

  // The summary row takes a chip's place rather than being added below the full
  // set — otherwise capping a four-booking day would produce a cell exactly as
  // tall as showing all four, and the cap would buy nothing where it matters.
  it('gives the summary row a chip’s place once something must be hidden', () => {
    expect(chipsForCell(entries(4), false, 3)).toMatchObject({ hiddenCount: 2 });
    expect(chipsForCell(entries(4), false, 3).shown).toHaveLength(2);
  });

  it('accounts for every hidden booking, however many there are', () => {
    const capped = chipsForCell(entries(40), false, 3);
    expect(capped.shown).toHaveLength(2);
    expect(capped.shown.length + capped.hiddenCount).toBe(40);
  });

  it('shows all of them once the day is expanded', () => {
    expect(chipsForCell(entries(40), true, 3)).toMatchObject({ hiddenCount: 0 });
    expect(chipsForCell(entries(40), true, 3).shown).toHaveLength(40);
  });

  it('keeps the day’s own order', () => {
    expect(chipsForCell(entries(4), false, 3).shown.map((e) => e.booking.id)).toEqual(['b0', 'b1']);
  });
});

describe('overlapping bookings in a day column', () => {
  const day = (...spans: [number, number][]) =>
    buildDayCells(
      spans.map(([from, to], i) =>
        booking({
          id: `b${i}`,
          startsAtUtc: localInstant(2026, 8, 24, Math.floor(from), (from % 1) * 60),
          endsAtUtc: localInstant(2026, 8, 24, Math.floor(to), (to % 1) * 60),
        }),
      ),
      { start: '2026-09-24', end: '2026-09-24' },
      '2026-09-01',
      '2026-09-18',
    )[0].entries;

  it('gives a lone booking the whole column', () => {
    expect(layOutDay(day([9, 10]))).toEqual([
      expect.objectContaining({ column: 0, columnCount: 1 }),
    ]);
  });

  // Without this the two were drawn on top of each other and whichever came
  // last in the DOM simply hid the other.
  it('splits two overlapping bookings into side-by-side columns', () => {
    const laid = layOutDay(day([9, 11], [10, 12]));

    expect(laid.map((p) => p.column)).toEqual([0, 1]);
    expect(laid.every((p) => p.columnCount === 2)).toBe(true);
  });

  // Touching is not overlapping: back-to-back bookings each keep the full width.
  it('treats a booking starting exactly when another ends as separate', () => {
    const laid = layOutDay(day([9, 10], [10, 11]));

    expect(laid.every((p) => p.column === 0 && p.columnCount === 1)).toBe(true);
  });

  it('widens to three when three genuinely overlap', () => {
    const laid = layOutDay(day([9, 12], [10, 12], [11, 12]));

    expect(laid.map((p) => p.column)).toEqual([0, 1, 2]);
    expect(laid.every((p) => p.columnCount === 3)).toBe(true);
  });

  // The count is the cluster's, not the day's busiest moment — a crowded
  // morning must not squeeze the afternoon's single booking into a sliver.
  it('keeps clusters independent of one another', () => {
    const laid = layOutDay(day([9, 11], [10, 12], [15, 16]));

    expect(laid.find((p) => p.entry.booking.id === 'b2')).toMatchObject({
      column: 0,
      columnCount: 1,
    });
  });

  // A freed column is reused rather than the day growing a new one for every
  // booking after the first overlap.
  it('reuses a column once its occupant has ended', () => {
    const laid = layOutDay(day([9, 11], [10, 12], [11, 13]));

    expect(laid.find((p) => p.entry.booking.id === 'b2')?.column).toBe(0);
    expect(laid.every((p) => p.columnCount === 2)).toBe(true);
  });
});

describe('chips too short for two lines', () => {
  const entryOf = (fromHour: number, toHour: number) =>
    buildDayCells(
      [
        booking({
          startsAtUtc: localInstant(2026, 8, 24, Math.floor(fromHour), (fromHour % 1) * 60),
          endsAtUtc: localInstant(2026, 8, 24, Math.floor(toHour), (toHour % 1) * 60),
        }),
      ],
      { start: '2026-09-24', end: '2026-09-24' },
      '2026-09-01',
      '2026-09-18',
    )[0].entries[0];

  // A week row is a fixed 56px per hour, so this is a duration rather than a
  // pixel measurement: two lines need about 40px, which is roughly 43 minutes.
  it('treats a booking under three quarters of an hour as compact', () => {
    expect(isCompactChip(entryOf(9, 9.5))).toBe(true);
    expect(isCompactChip(entryOf(9, 9.25))).toBe(true);
  });

  it('leaves a longer booking its two-line shape', () => {
    expect(isCompactChip(entryOf(9, 10))).toBe(false);
    expect(isCompactChip(entryOf(9, 9.75))).toBe(false);
  });
});

describe('the week hour axis', () => {
  const dayWith = (startHour: number, endHour: number) =>
    buildDayCells(
      [
        booking({
          startsAtUtc: localInstant(2026, 8, 24, startHour, 0),
          endsAtUtc: localInstant(2026, 8, 24, endHour, 0),
        }),
      ],
      { start: '2026-09-21', end: '2026-09-27' },
      '2026-09-01',
      '2026-09-18',
    );

  it('defaults to the design’s 08:00–18:00', () => {
    expect(weekHourRange(buildDayCells([], { start: '2026-09-21', end: '2026-09-27' }, '2026-09-01', '2026-09-18'))).toEqual(
      { startHour: 8, endHour: 18 },
    );
  });

  // The design's fixed hours would put an early or late booking outside the
  // grid, where it would simply be invisible — a worse failure than a taller
  // grid, and one a viewer in a different zone from the resource can cause.
  it('widens to contain a booking outside the default hours', () => {
    expect(weekHourRange(dayWith(6, 7))).toMatchObject({ startHour: 6, endHour: 18 });
    expect(weekHourRange(dayWith(20, 22))).toMatchObject({ startHour: 8, endHour: 22 });
  });

  it('positions a chip as a percentage of the visible hour window', () => {
    const entry = dayWith(10, 11)[3].entries[0]; // Thursday Sep 24
    const style = hourSpanStylePercent(entry, { startHour: 8, endHour: 18 });

    // 10:00 is 2h into a 10h window; a one-hour booking is a tenth of it.
    expect(style.top).toBe('20%');
    expect(style.height).toBe('10%');
  });

  // The alignment contract, stated as the invariant it actually is: an hour's
  // label and a chip beginning at that hour are the same offset. Positioning
  // them through one function is what makes this true by construction rather
  // than by two calculations happening to agree — the 2026-09-18 bug was
  // exactly those two disagreeing.
  it('puts an hour label at the same offset as a chip starting on that hour', () => {
    const hours = { startHour: 8, endHour: 18 };

    for (let hour = hours.startHour; hour < hours.endHour; hour++) {
      const entry = dayWith(hour, hour + 1)[3].entries[0];
      expect(`${hourOffsetPercent(hour, hours)}%`).toBe(hourSpanStylePercent(entry, hours).top);
    }
  });

  it('places the window’s own ends at 0% and 100%', () => {
    const hours = { startHour: 8, endHour: 18 };

    expect(hourOffsetPercent(8, hours)).toBe(0);
    expect(hourOffsetPercent(18, hours)).toBe(100);
    expect(minuteOffsetPercent(13 * 60, hours)).toBe(50);
  });
});

describe('viewerToday', () => {
  it('reads the calendar date in the viewer’s own zone', () => {
    // 23:30 local on Sep 24 is Sep 24 to the viewer whatever the UTC date is —
    // which is the whole reason this does not go through toISOString().
    expect(viewerToday(new Date(2026, 8, 24, 23, 30))).toBe('2026-09-24');
    expect(viewerToday(new Date(2026, 8, 24, 0, 30))).toBe('2026-09-24');
  });
});
