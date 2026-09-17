import {
  buildAxisTicks,
  buildDayRows,
  computeAxis,
  datesInRange,
  minuteSpanStylePercent,
  resourceLocalMinutesToUtc,
  splitIntervalByLocalDay,
} from './availability-grid';
import { BookableInterval } from './availability.models';
import { utcToResourceLocal } from './local-date';

function interval(startUtc: string, endUtc: string, remainingCapacity = 1): BookableInterval {
  return { startUtc, endUtc, remainingCapacity };
}

// 2026-09-21 is a Monday, 09-22 a Tuesday, 09-23 a Wednesday — the dates every
// test below uses, so one weekday window per day is enough.
const WEEKDAY_WINDOWS = [
  { weekday: 'Monday', opensAt: '09:00:00', closesAt: '17:00:00' },
  { weekday: 'Tuesday', opensAt: '09:00:00', closesAt: '17:00:00' },
  { weekday: 'Wednesday', opensAt: '09:00:00', closesAt: '17:00:00' },
];

// minuteSpanStylePercent doesn't care about startUtc/endUtc at all — this fills
// them with an arbitrary but valid ISO string just to satisfy the type.
function fakeSegment(overrides: Partial<import('./availability-grid').DaySegment> = {}) {
  return {
    date: '2026-09-21',
    startMinutes: 9 * 60,
    endMinutes: 11 * 60,
    startUtc: '2026-09-21T09:00:00Z',
    endUtc: '2026-09-21T11:00:00Z',
    remainingCapacity: 1,
    ...overrides,
  };
}

describe('availability-grid', () => {
  describe('splitIntervalByLocalDay', () => {
    it('returns one segment for an interval that stays within a single local day, reusing the interval\'s own UTC bounds exactly', () => {
      const segments = splitIntervalByLocalDay(
        interval('2026-09-21T13:00:00Z', '2026-09-21T21:00:00Z'),
        'UTC',
      );
      expect(segments).toEqual([
        {
          date: '2026-09-21',
          startMinutes: 13 * 60,
          endMinutes: 21 * 60,
          startUtc: '2026-09-21T13:00:00.000Z',
          endUtc: '2026-09-21T21:00:00.000Z',
          remainingCapacity: 1,
        },
      ]);
    });

    it('splits an interval that crosses one local midnight into two segments, with continuous UTC boundaries', () => {
      // 22:00 Sep 21 -> 02:00 Sep 22, both UTC (resource in UTC too, for a
      // simple case) — an overnight span, e.g. a 24-hour lab slot resource.
      const segments = splitIntervalByLocalDay(
        interval('2026-09-21T22:00:00Z', '2026-09-22T02:00:00Z', 2),
        'UTC',
      );
      expect(segments).toEqual([
        {
          date: '2026-09-21',
          startMinutes: 22 * 60,
          endMinutes: 1440,
          startUtc: '2026-09-21T22:00:00.000Z',
          endUtc: '2026-09-22T00:00:00.000Z', // local midnight
          remainingCapacity: 2,
        },
        {
          date: '2026-09-22',
          startMinutes: 0,
          endMinutes: 2 * 60,
          startUtc: '2026-09-22T00:00:00.000Z', // matches the segment above — no gap, no overlap
          endUtc: '2026-09-22T02:00:00.000Z',
          remainingCapacity: 2,
        },
      ]);
    });

    it('splits an interval that spans multiple full days into one segment per day, chaining startUtc/endUtc across all of them', () => {
      const segments = splitIntervalByLocalDay(
        interval('2026-09-21T10:00:00Z', '2026-09-24T06:00:00Z'),
        'UTC',
      );
      expect(segments).toEqual([
        {
          date: '2026-09-21',
          startMinutes: 10 * 60,
          endMinutes: 1440,
          startUtc: '2026-09-21T10:00:00.000Z',
          endUtc: '2026-09-22T00:00:00.000Z',
          remainingCapacity: 1,
        },
        {
          date: '2026-09-22',
          startMinutes: 0,
          endMinutes: 1440,
          startUtc: '2026-09-22T00:00:00.000Z',
          endUtc: '2026-09-23T00:00:00.000Z',
          remainingCapacity: 1,
        },
        {
          date: '2026-09-23',
          startMinutes: 0,
          endMinutes: 1440,
          startUtc: '2026-09-23T00:00:00.000Z',
          endUtc: '2026-09-24T00:00:00.000Z',
          remainingCapacity: 1,
        },
        {
          date: '2026-09-24',
          startMinutes: 0,
          endMinutes: 6 * 60,
          startUtc: '2026-09-24T00:00:00.000Z',
          endUtc: '2026-09-24T06:00:00.000Z', // the interval's own end, exactly
          remainingCapacity: 1,
        },
      ]);
    });

    it('produces no segment for the end day when the interval ends exactly at local midnight', () => {
      const segments = splitIntervalByLocalDay(
        interval('2026-09-21T22:00:00Z', '2026-09-22T00:00:00Z'),
        'UTC',
      );
      expect(segments).toEqual([
        {
          date: '2026-09-21',
          startMinutes: 22 * 60,
          endMinutes: 1440,
          startUtc: '2026-09-21T22:00:00.000Z',
          endUtc: '2026-09-22T00:00:00.000Z',
          remainingCapacity: 1,
        },
      ]);
    });
  });

  describe('datesInRange', () => {
    it('is inclusive of both ends', () => {
      expect(datesInRange('2026-09-21', '2026-09-23')).toEqual(['2026-09-21', '2026-09-22', '2026-09-23']);
    });

    it('is a single date when from equals to', () => {
      expect(datesInRange('2026-09-21', '2026-09-21')).toEqual(['2026-09-21']);
    });
  });

  describe('buildDayRows', () => {
    it('assigns each interval to its own local day, including a day with no intervals', () => {
      const rows = buildDayRows(
        ['2026-09-21', '2026-09-22', '2026-09-23'],
        [interval('2026-09-21T08:00:00Z', '2026-09-21T12:00:00Z'), interval('2026-09-23T09:00:00Z', '2026-09-23T15:00:00Z')],
        'UTC',
      );

      expect(rows).toEqual([
        {
          date: '2026-09-21',
          segments: [
            {
              date: '2026-09-21',
              startMinutes: 8 * 60,
              endMinutes: 12 * 60,
              startUtc: '2026-09-21T08:00:00.000Z',
              endUtc: '2026-09-21T12:00:00.000Z',
              remainingCapacity: 1,
            },
          ],
          // Empty with no opening windows passed: without them there is no
          // "inside opening hours" for a gap to be inside of.
          unbookable: [],
        },
        { date: '2026-09-22', segments: [], unbookable: [] },
        {
          date: '2026-09-23',
          segments: [
            {
              date: '2026-09-23',
              startMinutes: 9 * 60,
              endMinutes: 15 * 60,
              startUtc: '2026-09-23T09:00:00.000Z',
              endUtc: '2026-09-23T15:00:00.000Z',
              remainingCapacity: 1,
            },
          ],
          unbookable: [],
        },
      ]);
    });

    // Why an opening-hours span isn't bookable — derived client-side, since
    // GET /resources/{id}/availability answers with bookable time only.
    describe('unbookable spans', () => {
      it('labels a gap between two bookable spans as booked', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [
            interval('2026-09-21T09:00:00Z', '2026-09-21T11:00:00Z'),
            interval('2026-09-21T13:00:00Z', '2026-09-21T17:00:00Z'),
          ],
          'UTC',
          WEEKDAY_WINDOWS,
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 11 * 60, endMinutes: 13 * 60, kind: 'booked' },
        ]);
      });

      it('labels a gap a blackout covers as blacked out', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [
            interval('2026-09-21T09:00:00Z', '2026-09-21T11:00:00Z'),
            interval('2026-09-21T13:00:00Z', '2026-09-21T17:00:00Z'),
          ],
          'UTC',
          WEEKDAY_WINDOWS,
          [{ startUtc: '2026-09-21T11:00:00Z', endUtc: '2026-09-21T13:00:00Z' }],
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 11 * 60, endMinutes: 13 * 60, kind: 'blackout' },
        ]);
      });

      // A blackout covering only part of a gap splits it: the covered part is
      // "Unavailable", what remains is still "Booked".
      it('splits one gap into its blacked-out and merely-booked parts', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [
            interval('2026-09-21T09:00:00Z', '2026-09-21T10:00:00Z'),
            interval('2026-09-21T14:00:00Z', '2026-09-21T17:00:00Z'),
          ],
          'UTC',
          WEEKDAY_WINDOWS,
          [{ startUtc: '2026-09-21T11:00:00Z', endUtc: '2026-09-21T13:00:00Z' }],
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 10 * 60, endMinutes: 11 * 60, kind: 'booked' },
          { startMinutes: 11 * 60, endMinutes: 13 * 60, kind: 'blackout' },
          { startMinutes: 13 * 60, endMinutes: 14 * 60, kind: 'booked' },
        ]);
      });

      it('covers a whole day that is open but entirely blacked out', () => {
        const rows = buildDayRows(['2026-09-21'], [], 'UTC', WEEKDAY_WINDOWS, [
          { startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T18:00:00Z' },
        ]);

        expect(rows[0].segments).toEqual([]);
        expect(rows[0].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 17 * 60, kind: 'blackout' },
        ]);
      });

      // A closed day is not "unavailable", it is simply not open — the row's
      // own empty-day label already says so, and drawing a bar across hours
      // the resource never opens would claim something untrue.
      it('says nothing about a weekday the resource has no window for', () => {
        const rows = buildDayRows(['2026-09-26'], [], 'UTC', WEEKDAY_WINDOWS, [
          { startUtc: '2026-09-26T09:00:00Z', endUtc: '2026-09-26T17:00:00Z' },
        ]);

        expect(rows[0].unbookable).toEqual([]);
      });

      it('never labels time outside the opening window', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [interval('2026-09-21T09:00:00Z', '2026-09-21T17:00:00Z')],
          'UTC',
          WEEKDAY_WINDOWS,
        );

        // Fully bookable inside 09:00-17:00 — the hours either side are
        // closed, not unbookable.
        expect(rows[0].unbookable).toEqual([]);
      });

      // Decision `0022`: a window closing at 23:59:59 means the following
      // midnight, so the last minute of the day must not read as an
      // unbookable sliver.
      it('treats a 23:59:59 close as midnight', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [interval('2026-09-21T09:00:00Z', '2026-09-22T00:00:00Z')],
          'UTC',
          [{ weekday: 'Monday', opensAt: '09:00:00', closesAt: '23:59:59' }],
        );

        expect(rows[0].unbookable).toEqual([]);
      });

      // The owner's own case, 2026-09-17: the seeded 3D Printer's weekday
      // hours are two *touching* windows (09:00-12:00 and 12:00-17:00), which
      // cut a single 11:00-13:00 blackout into "Unavailable 11-12" and
      // "Unavailable 12-13". Contiguous windows describe one continuous
      // opening span, so there is no closed time at 12:00 for the split to
      // stand for.
      it('does not split a span at a boundary between two touching windows', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [
            interval('2026-09-21T09:00:00Z', '2026-09-21T11:00:00Z'),
            interval('2026-09-21T13:00:00Z', '2026-09-21T17:00:00Z'),
          ],
          'UTC',
          [
            { weekday: 'Monday', opensAt: '09:00:00', closesAt: '12:00:00' },
            { weekday: 'Monday', opensAt: '12:00:00', closesAt: '17:00:00' },
          ],
          [{ startUtc: '2026-09-21T11:00:00Z', endUtc: '2026-09-21T13:00:00Z' }],
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 11 * 60, endMinutes: 13 * 60, kind: 'blackout' },
        ]);
      });

      it('merges overlapping windows too, not just touching ones', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [],
          'UTC',
          [
            { weekday: 'Monday', opensAt: '09:00:00', closesAt: '13:00:00' },
            { weekday: 'Monday', opensAt: '11:00:00', closesAt: '17:00:00' },
          ],
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });

      // The other way the same stretch could be cut in two: separate blackout
      // rows that abut. One reason, one pill.
      it('shows one pill for two blackouts that meet end to end', () => {
        const rows = buildDayRows(['2026-09-21'], [], 'UTC', WEEKDAY_WINDOWS, [
          { startUtc: '2026-09-21T10:00:00Z', endUtc: '2026-09-21T12:00:00Z' },
          { startUtc: '2026-09-21T12:00:00Z', endUtc: '2026-09-21T14:00:00Z' },
        ]);

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 10 * 60, kind: 'booked' },
          { startMinutes: 10 * 60, endMinutes: 14 * 60, kind: 'blackout' },
          { startMinutes: 14 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });

      it('shows one pill for two overlapping blackouts', () => {
        const rows = buildDayRows(['2026-09-21'], [], 'UTC', WEEKDAY_WINDOWS, [
          { startUtc: '2026-09-21T10:00:00Z', endUtc: '2026-09-21T13:00:00Z' },
          { startUtc: '2026-09-21T11:00:00Z', endUtc: '2026-09-21T14:00:00Z' },
        ]);

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 10 * 60, kind: 'booked' },
          { startMinutes: 10 * 60, endMinutes: 14 * 60, kind: 'blackout' },
          { startMinutes: 14 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });

      // Merging must not run across a change of reason: a blackout ending
      // where booked time begins is still two different answers.
      it('keeps a blackout and the booked time next to it apart', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [interval('2026-09-21T09:00:00Z', '2026-09-21T11:00:00Z')],
          'UTC',
          WEEKDAY_WINDOWS,
          [{ startUtc: '2026-09-21T11:00:00Z', endUtc: '2026-09-21T13:00:00Z' }],
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 11 * 60, endMinutes: 13 * 60, kind: 'blackout' },
          { startMinutes: 13 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });

      // A split shift: the gap between two windows is closed time, not
      // unbookable time, so merging across it would mislabel it — which is
      // why only *touching* windows merge.
      it('leaves the gap between two same-day windows alone', () => {
        const rows = buildDayRows(
          ['2026-09-21'],
          [interval('2026-09-21T09:00:00Z', '2026-09-21T12:00:00Z')],
          'UTC',
          [
            { weekday: 'Monday', opensAt: '09:00:00', closesAt: '12:00:00' },
            { weekday: 'Monday', opensAt: '14:00:00', closesAt: '17:00:00' },
          ],
        );

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 14 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });

      // A blackout is a UTC span like any other and can cover parts of two
      // local days — it goes through the same splitter bookable intervals do.
      it('applies a blackout that spans local midnight to both days', () => {
        const rows = buildDayRows(
          ['2026-09-21', '2026-09-22'],
          [],
          'UTC',
          WEEKDAY_WINDOWS,
          [{ startUtc: '2026-09-21T16:00:00Z', endUtc: '2026-09-22T10:00:00Z' }],
        );

        // Nothing is bookable on either day here, so the whole window is
        // accounted for: the blackout's own hours as "Unavailable", the rest
        // as "Booked".
        expect(rows[0].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 16 * 60, kind: 'booked' },
          { startMinutes: 16 * 60, endMinutes: 17 * 60, kind: 'blackout' },
        ]);
        expect(rows[1].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 10 * 60, kind: 'blackout' },
          { startMinutes: 10 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });

      it('reads both windows and blackouts in the resource\'s own timezone', () => {
        // 13:00-15:00Z is 09:00-11:00 in New York, so a blackout there covers
        // the first two hours of a 09:00-17:00 local window.
        const rows = buildDayRows(['2026-09-21'], [], 'America/New_York', WEEKDAY_WINDOWS, [
          { startUtc: '2026-09-21T13:00:00Z', endUtc: '2026-09-21T15:00:00Z' },
        ]);

        expect(rows[0].unbookable).toEqual([
          { startMinutes: 9 * 60, endMinutes: 11 * 60, kind: 'blackout' },
          { startMinutes: 11 * 60, endMinutes: 17 * 60, kind: 'booked' },
        ]);
      });
    });

    it('sorts multiple segments on the same day by start time', () => {
      const rows = buildDayRows(
        ['2026-09-21'],
        [interval('2026-09-21T13:00:00Z', '2026-09-21T18:00:00Z'), interval('2026-09-21T08:00:00Z', '2026-09-21T12:00:00Z')],
        'UTC',
      );

      expect(rows[0].segments.map((s) => s.startMinutes)).toEqual([8 * 60, 13 * 60]);
    });

    it('puts both halves of a midnight-crossing interval on their respective day rows', () => {
      const rows = buildDayRows(
        ['2026-09-21', '2026-09-22'],
        [interval('2026-09-21T22:00:00Z', '2026-09-22T02:00:00Z')],
        'UTC',
      );

      expect(rows[0].segments).toHaveLength(1);
      expect(rows[0].segments[0].endMinutes).toBe(1440);
      expect(rows[1].segments).toHaveLength(1);
      expect(rows[1].segments[0].startMinutes).toBe(0);
    });
  });

  describe('computeAxis', () => {
    it('floors the earliest start and ceils the latest end to whole hours', () => {
      const rows = buildDayRows(
        ['2026-09-21', '2026-09-22'],
        [interval('2026-09-21T08:15:00Z', '2026-09-21T11:00:00Z'), interval('2026-09-22T09:00:00Z', '2026-09-22T17:45:00Z')],
        'UTC',
      );
      expect(computeAxis(rows)).toEqual({ startMinutes: 8 * 60, endMinutes: 18 * 60 });
    });

    it('falls back to a default 08:00-18:00 axis when nothing is bookable anywhere in the range', () => {
      const rows = buildDayRows(['2026-09-21', '2026-09-22'], [], 'UTC');
      expect(computeAxis(rows)).toEqual({ startMinutes: 8 * 60, endMinutes: 18 * 60 });
    });

    it('never collapses to a zero-width axis for a single-instant edge case', () => {
      const rows = buildDayRows(['2026-09-21'], [interval('2026-09-21T08:00:00Z', '2026-09-21T08:00:00Z')], 'UTC');
      const axis = computeAxis(rows);
      expect(axis.endMinutes).toBeGreaterThan(axis.startMinutes);
    });
  });

  describe('buildAxisTicks', () => {
    it('produces a tick every 2 hours across the axis', () => {
      expect(buildAxisTicks({ startMinutes: 8 * 60, endMinutes: 18 * 60 })).toEqual([
        8 * 60,
        10 * 60,
        12 * 60,
        14 * 60,
        16 * 60,
        18 * 60,
      ]);
    });

    it('starts from the first even hour at or after an odd axis start', () => {
      expect(buildAxisTicks({ startMinutes: 7 * 60, endMinutes: 11 * 60 })).toEqual([8 * 60, 10 * 60]);
    });
  });

  describe('minuteSpanStylePercent', () => {
    const axis = { startMinutes: 8 * 60, endMinutes: 18 * 60 }; // 10-hour span

    it('positions a segment proportionally within the axis', () => {
      const style = minuteSpanStylePercent(fakeSegment({ startMinutes: 9 * 60, endMinutes: 11 * 60 }), axis);
      expect(style.leftPercent).toBeCloseTo(10); // (9-8)/10 * 100
      expect(style.widthPercent).toBeCloseTo(20); // 2/10 * 100
    });

    it('clamps a segment that runs past the axis bounds instead of overflowing it', () => {
      const style = minuteSpanStylePercent(fakeSegment({ startMinutes: 6 * 60, endMinutes: 20 * 60 }), axis);
      expect(style.leftPercent).toBe(0);
      expect(style.widthPercent).toBe(100);
    });
  });

  // Item 7: a flat "add local minutes to segment.startUtc" offset drifts by
  // exactly the DST delta whenever a transition falls between the segment's
  // own start and the selected local time. Each fixture's segment starts at
  // that local day's own midnight (so the flat-offset bug this replaced
  // would have been wrong for every target after the transition) and
  // asserts the *round-trip*: converting the result back through the
  // already-trusted utcToResourceLocal must recover exactly the local time
  // that was asked for.
  describe('resourceLocalMinutesToUtc', () => {
    it('matches the old flat-offset behaviour on an ordinary day with no transition', () => {
      const segment = fakeSegment({
        date: '2026-09-21',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-09-20T22:00:00.000Z', // 2026-09-21 00:00 CEST (+2) — well clear of any DST transition
      });
      const targetMinutes = 9 * 60 + 30; // 09:30 local (+2) = 07:30 UTC, same calendar day
      const result = resourceLocalMinutesToUtc(segment, targetMinutes, 'Europe/Sarajevo');
      expect(result).toBe('2026-09-21T07:30:00.000Z');
      expect(utcToResourceLocal(result, 'Europe/Sarajevo')).toEqual({ date: '2026-09-21', minutesOfDay: targetMinutes });
    });

    it('Europe/Sarajevo spring-forward: a local time after the gap converts to the correct, non-flat-offset UTC instant', () => {
      const segment = fakeSegment({
        date: '2026-03-29',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-03-28T23:00:00.000Z', // 2026-03-29 00:00 CET (+1), the day of the transition
      });
      const targetMinutes = 3 * 60 + 30; // 03:30 — only exists as CEST (+2), after the 02:00->03:00 jump
      const result = resourceLocalMinutesToUtc(segment, targetMinutes, 'Europe/Sarajevo');

      expect(result).toBe('2026-03-29T01:30:00.000Z');
      expect(utcToResourceLocal(result, 'Europe/Sarajevo')).toEqual({ date: '2026-03-29', minutesOfDay: targetMinutes });
      // The bug this replaces: segment.startUtc + 210 raw minutes lands an hour late.
      expect(result).not.toBe('2026-03-29T02:30:00.000Z');
    });

    it('Europe/Sarajevo fall-back: an ambiguous local time resolves to the earlier candidate, matching decisions/0024', () => {
      const segment = fakeSegment({
        date: '2026-10-25',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-10-24T22:00:00.000Z', // 2026-10-25 00:00 CEST (+2), the day of the transition
      });
      const targetMinutes = 2 * 60 + 30; // 02:30 — occurs twice, once CEST then once CET

      const result = resourceLocalMinutesToUtc(segment, targetMinutes, 'Europe/Sarajevo');

      expect(result).toBe('2026-10-25T00:30:00.000Z'); // the earlier (CEST) occurrence
      expect(utcToResourceLocal(result, 'Europe/Sarajevo')).toEqual({ date: '2026-10-25', minutesOfDay: targetMinutes });
    });

    it('Europe/Sarajevo fall-back: a local time after the ambiguous hour still converts correctly', () => {
      const segment = fakeSegment({
        date: '2026-10-25',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-10-24T22:00:00.000Z',
      });
      const targetMinutes = 4 * 60; // 04:00, safely after the fall-back has resolved

      const result = resourceLocalMinutesToUtc(segment, targetMinutes, 'Europe/Sarajevo');

      expect(result).toBe('2026-10-25T03:00:00.000Z');
      expect(utcToResourceLocal(result, 'Europe/Sarajevo')).toEqual({ date: '2026-10-25', minutesOfDay: targetMinutes });
    });

    it('Australia/Lord_Howe: a 30-minute DST delta (not 60) still converts correctly', () => {
      const segment = fakeSegment({
        date: '2026-10-04',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-10-03T13:30:00.000Z', // 2026-10-04 00:00 Lord Howe standard time
      });
      const targetMinutes = 3 * 60; // 03:00 — after the 02:00->02:30 (30-minute) spring-forward gap

      const result = resourceLocalMinutesToUtc(segment, targetMinutes, 'Australia/Lord_Howe');

      expect(result).toBe('2026-10-03T16:00:00.000Z');
      expect(utcToResourceLocal(result, 'Australia/Lord_Howe')).toEqual({ date: '2026-10-04', minutesOfDay: targetMinutes });
      // The bug this replaces would be off by the 30-minute delta, not 60.
      expect(result).not.toBe('2026-10-03T16:30:00.000Z');
    });

    it('Australia/Lord_Howe fall-back: an ambiguous local time resolves to the earlier candidate', () => {
      const segment = fakeSegment({
        date: '2026-04-05',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-04-04T13:00:00.000Z', // 2026-04-05 00:00 Lord Howe daylight time
      });
      const targetMinutes = 1 * 60 + 30; // 01:30 — ambiguous, occurs once DST then once standard

      const result = resourceLocalMinutesToUtc(segment, targetMinutes, 'Australia/Lord_Howe');

      expect(result).toBe('2026-04-04T14:30:00.000Z');
      expect(utcToResourceLocal(result, 'Australia/Lord_Howe')).toEqual({ date: '2026-04-05', minutesOfDay: targetMinutes });
    });

    it('does not crash on a local time inside a spring-forward gap that never happened', () => {
      const segment = fakeSegment({
        date: '2026-10-04',
        startMinutes: 0,
        endMinutes: 24 * 60,
        startUtc: '2026-10-03T13:30:00.000Z',
      });
      // 02:15 never occurs that day (the 30-minute gap is 02:00-02:29).
      expect(() => resourceLocalMinutesToUtc(segment, 2 * 60 + 15, 'Australia/Lord_Howe')).not.toThrow();
    });
  });
});
