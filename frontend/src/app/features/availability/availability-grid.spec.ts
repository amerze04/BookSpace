import {
  buildAxisTicks,
  buildDayRows,
  computeAxis,
  datesInRange,
  minuteSpanStylePercent,
  splitIntervalByLocalDay,
} from './availability-grid';
import { BookableInterval } from './availability.models';

function interval(startUtc: string, endUtc: string, remainingCapacity = 1): BookableInterval {
  return { startUtc, endUtc, remainingCapacity };
}

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
        },
        { date: '2026-09-22', segments: [] },
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
        },
      ]);
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
});
