import {
  addDays,
  addMinutesToUtc,
  formatLocalDate,
  formatLocalDateWithWeekday,
  formatLocalDateWithWeekdayAndYear,
  formatMinutesOfDay,
  rangeLengthDays,
  resourceLocalToday,
  toWholeSecondUtcIso,
  utcToResourceLocal,
} from './local-date';

describe('local-date', () => {
  describe('addDays', () => {
    it('adds within a month', () => {
      expect(addDays('2026-09-21', 6)).toBe('2026-09-27');
    });

    it('crosses a month boundary', () => {
      expect(addDays('2026-09-28', 6)).toBe('2026-10-04');
    });

    it('crosses a year boundary', () => {
      expect(addDays('2026-12-28', 6)).toBe('2027-01-03');
    });

    it('crosses a leap-year February 29th', () => {
      expect(addDays('2028-02-27', 3)).toBe('2028-03-01');
    });

    it('subtracts (negative days) for the "previous week" navigation case', () => {
      expect(addDays('2026-09-21', -7)).toBe('2026-09-14');
    });
  });

  describe('rangeLengthDays', () => {
    it('is 1 for the same date on both ends, inclusive of both', () => {
      expect(rangeLengthDays('2026-09-21', '2026-09-21')).toBe(1);
    });

    it('is 7 for a Mon-Sun week', () => {
      expect(rangeLengthDays('2026-09-21', '2026-09-27')).toBe(7);
    });

    it('is 90 for a range exactly at the backend\'s cap', () => {
      expect(rangeLengthDays('2026-01-01', '2026-03-31')).toBe(90);
    });
  });

  describe('formatLocalDate', () => {
    it('formats as "Mon D, YYYY"', () => {
      expect(formatLocalDate('2026-09-21')).toBe('Sep 21, 2026');
    });

    it('is not shifted by the viewer\'s own local timezone', () => {
      // A UTC-anchored Date for a Jan 1st formatted with timeZone: 'UTC'
      // must stay Jan 1st regardless of where this test happens to run —
      // the bug this guards against is exactly a browser west of UTC
      // reinterpreting midnight as the previous day.
      expect(formatLocalDate('2026-01-01')).toBe('Jan 1, 2026');
    });
  });

  describe('resourceLocalToday', () => {
    it('returns a Y-M-D string for the given IANA zone', () => {
      const today = resourceLocalToday('Europe/Sarajevo');
      expect(today).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });

    it('can disagree with the viewer\'s own local date near a day boundary', () => {
      // Not asserting a specific date (that would be a flaky, clock-dependent
      // test) — just that two very different zones don't have to agree,
      // proving this reads the *resource's* zone rather than the browser's.
      const tokyo = resourceLocalToday('Asia/Tokyo');
      const honolulu = resourceLocalToday('Pacific/Honolulu');
      expect(tokyo).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      expect(honolulu).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });
  });

  describe('addMinutesToUtc', () => {
    it('adds a positive offset', () => {
      expect(addMinutesToUtc('2026-09-21T08:00:00Z', 90)).toBe('2026-09-21T09:30:00.000Z');
    });

    it('adds a negative offset', () => {
      expect(addMinutesToUtc('2026-09-21T08:00:00Z', -30)).toBe('2026-09-21T07:30:00.000Z');
    });

    it('crosses a UTC day boundary', () => {
      expect(addMinutesToUtc('2026-09-21T23:00:00Z', 90)).toBe('2026-09-22T00:30:00.000Z');
    });
  });

  describe('formatLocalDateWithWeekday', () => {
    it('formats as "Weekday, Mon D" with no year', () => {
      expect(formatLocalDateWithWeekday('2026-09-21')).toBe('Mon, Sep 21');
      expect(formatLocalDateWithWeekday('2026-09-27')).toBe('Sun, Sep 27');
    });
  });

  describe('formatLocalDateWithWeekdayAndYear', () => {
    it('formats as "Weekday, Mon D, YYYY"', () => {
      expect(formatLocalDateWithWeekdayAndYear('2026-09-24')).toBe('Thu, Sep 24, 2026');
    });
  });

  describe('utcToResourceLocal', () => {
    it('reads the date and minutes-of-day in the given zone, not UTC', () => {
      // 13:00 UTC is 15:00 in Europe/Sarajevo (UTC+2 in September, no DST
      // change involved) — same calendar date either way, different clock.
      const result = utcToResourceLocal('2026-09-21T13:00:00Z', 'Europe/Sarajevo');
      expect(result.date).toBe('2026-09-21');
      expect(result.minutesOfDay).toBe(15 * 60);
    });

    it('can land on a different calendar date than the UTC instant', () => {
      // 23:30 UTC is 08:30 the *next* day in Asia/Tokyo (UTC+9) — the case
      // that makes per-day segment splitting necessary in the grid.
      const result = utcToResourceLocal('2026-09-21T23:30:00Z', 'Asia/Tokyo');
      expect(result.date).toBe('2026-09-22');
      expect(result.minutesOfDay).toBe(8 * 60 + 30);
    });

    it('reports local midnight as minute 0, not 24:00, via the explicit h23 cycle', () => {
      const result = utcToResourceLocal('2026-09-21T00:00:00Z', 'UTC');
      expect(result.date).toBe('2026-09-21');
      expect(result.minutesOfDay).toBe(0);
    });
  });

  describe('formatMinutesOfDay', () => {
    it('formats an ordinary time of day', () => {
      expect(formatMinutesOfDay(8 * 60)).toBe('08:00');
      expect(formatMinutesOfDay(18 * 60 + 30)).toBe('18:30');
    });

    it('formats end-of-day (1440) as "24:00", not "00:00"', () => {
      // A segment that runs to local midnight is a real, distinct case
      // (decision 0022's "ClosesAt = 23:59:59 means the following
      // midnight") — "00:00" would misread as the day just starting.
      expect(formatMinutesOfDay(24 * 60)).toBe('24:00');
    });
  });

  // What an instant looks like once it leaves this app — into the booking URL
  // and from there into POST /bookings, which refuses fractional seconds
  // outright (CLAUDE.md §4.3).
  describe('toWholeSecondUtcIso', () => {
    it('drops the sub-second part toISOString always emits', () => {
      expect(toWholeSecondUtcIso('2026-09-21T08:00:00.000Z')).toBe('2026-09-21T08:00:00Z');
    });

    it('truncates rather than rounds, so an instant never moves forward', () => {
      expect(toWholeSecondUtcIso('2026-09-21T08:00:00.999Z')).toBe('2026-09-21T08:00:00Z');
    });

    it('converts an explicit offset to the same instant in UTC', () => {
      expect(toWholeSecondUtcIso('2026-09-21T10:00:00+02:00')).toBe('2026-09-21T08:00:00Z');
    });

    it('leaves an already-whole-second UTC instant untouched', () => {
      expect(toWholeSecondUtcIso('2026-09-21T08:00:00Z')).toBe('2026-09-21T08:00:00Z');
    });
  });
});
