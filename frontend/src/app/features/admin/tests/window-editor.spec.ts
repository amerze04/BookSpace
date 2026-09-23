import { AvailabilityWindowDetail } from '../../resources/models/resources.models';
import {
  END_OF_DAY,
  MINUTES_PER_DAY,
  WindowRow,
  closesAtMinutes,
  hasChanges,
  newRowKey,
  toInputTime,
  toRows,
  toWindowPayload,
  toWireTime,
  validateRows,
} from '../windows/window-editor';

// Admin console phase 4. The weekly editor's rules, tested where they live.
//
// These are worth having at this level rather than only through the component
// because what they encode is arithmetic and a convention — decision `0022`'s
// midnight rule in particular, which is the sort of thing that looks like an
// off-by-one bug unless it is pinned deliberately.

function row(overrides: Partial<WindowRow> = {}): WindowRow {
  return {
    key: newRowKey(),
    weekday: 'Monday',
    opensAt: '09:00',
    closesAt: '17:00',
    closesAtEndOfDay: false,
    ...overrides,
  };
}

function window(overrides: Partial<AvailabilityWindowDetail> = {}): AvailabilityWindowDetail {
  return {
    id: 'w1',
    weekday: 'Monday',
    opensAt: '09:00:00',
    closesAt: '17:00:00',
    ...overrides,
  };
}

describe('time conversion', () => {
  it('drops the wire seconds for an input and invents them again on the way out', () => {
    expect(toInputTime('09:30:00')).toBe('09:30');
    expect(toWireTime('09:30')).toBe('09:30:00');
  });
});

describe('toRows', () => {
  // **Decision `0022`.** A stored 23:59:59 is the convention for "until
  // midnight", not a time anybody typed — so it comes back as the flag rather
  // than as a closing time of 23:59, which is what an admin would then see and
  // reasonably believe.
  it('reads a stored 23:59:59 as the end-of-day flag, not as a time', () => {
    const [first] = toRows([window({ closesAt: END_OF_DAY })]);

    expect(first.closesAtEndOfDay).toBe(true);
    expect(first.closesAt).toBe('');
  });

  it('reads an ordinary closing time as a time, with the flag off', () => {
    const [first] = toRows([window({ closesAt: '17:00:00' })]);

    expect(first.closesAtEndOfDay).toBe(false);
    expect(first.closesAt).toBe('17:00');
  });

  // Monday first, matching every other place this app renders a week.
  it('orders the week from Monday, then by opening time', () => {
    const rows = toRows([
      window({ id: 'a', weekday: 'Sunday', opensAt: '10:00:00' }),
      window({ id: 'b', weekday: 'Monday', opensAt: '13:00:00' }),
      window({ id: 'c', weekday: 'Monday', opensAt: '09:00:00' }),
    ]);

    expect(rows.map((r) => `${r.weekday} ${r.opensAt}`)).toEqual([
      'Monday 09:00',
      'Monday 13:00',
      'Sunday 10:00',
    ]);
  });
});

describe('closesAtMinutes', () => {
  // The whole point of the convention: end-of-day reads as 1440, not 1439, or
  // every such window would fall one minute short of the day it covers.
  it('reads end-of-day as the following midnight rather than 23:59', () => {
    expect(closesAtMinutes(row({ closesAtEndOfDay: true }))).toBe(MINUTES_PER_DAY);
    expect(closesAtMinutes(row({ closesAt: '23:59' }))).toBe(23 * 60 + 59);
  });
});

describe('toWindowPayload', () => {
  it('writes the convention out as 23:59:59 for an end-of-day row', () => {
    const [first] = toWindowPayload([row({ closesAtEndOfDay: true, closesAt: '' })]);

    expect(first.closesAt).toBe(END_OF_DAY);
  });

  it('sends whole-second times, which the validator requires', () => {
    const [first] = toWindowPayload([row({ opensAt: '08:15', closesAt: '12:45' })]);

    expect(first.opensAt).toBe('08:15:00');
    expect(first.closesAt).toBe('12:45:00');
  });

  it('carries no client-side row key onto the wire', () => {
    const [first] = toWindowPayload([row()]);

    expect(Object.keys(first).sort()).toEqual(['closesAt', 'opensAt', 'weekday']);
  });
});

describe('validateRows', () => {
  it('accepts an ordinary weekly schedule', () => {
    expect(
      validateRows([
        row({ weekday: 'Monday', opensAt: '09:00', closesAt: '17:00' }),
        row({ weekday: 'Tuesday', opensAt: '09:00', closesAt: '17:00' }),
      ]),
    ).toEqual([]);
  });

  it('refuses a window that closes before it opens', () => {
    const subject = row({ opensAt: '17:00', closesAt: '09:00' });

    expect(validateRows([subject])[0].message).toContain('after the opening time');
  });

  it('refuses a window that closes exactly when it opens', () => {
    const subject = row({ opensAt: '09:00', closesAt: '09:00' });

    expect(validateRows([subject])).toHaveLength(1);
  });

  it('refuses a row with no opening time, and one with neither closing answer', () => {
    expect(validateRows([row({ opensAt: '' })])[0].message).toContain('opening time');
    expect(validateRows([row({ closesAt: '', closesAtEndOfDay: false })])[0].message).toContain(
      'closing time',
    );
  });

  // **The server's rule, restated exactly** — including the part that looks
  // like a bug. `ClosesAt` is exclusive, so a window ending when the next
  // begins is legal, and refusing it here would make this editor stricter than
  // the API it writes to.
  it('treats adjacency as legal, not as overlap', () => {
    expect(
      validateRows([
        row({ opensAt: '09:00', closesAt: '12:00' }),
        row({ opensAt: '12:00', closesAt: '17:00' }),
      ]),
    ).toEqual([]);
  });

  it('refuses two windows that genuinely overlap on one day', () => {
    const errors = validateRows([
      row({ opensAt: '09:00', closesAt: '13:00' }),
      row({ opensAt: '12:00', closesAt: '17:00' }),
    ]);

    expect(errors).toHaveLength(1);
    expect(errors[0].message).toContain('overlaps another Monday window');
  });

  // The failure a naive "compare adjacent pairs" implementation would miss if
  // it forgot to sort: a long window swallowing a short one entirely.
  it('catches a long window that swallows a shorter one', () => {
    const errors = validateRows([
      row({ opensAt: '08:00', closesAt: '18:00' }),
      row({ opensAt: '10:00', closesAt: '11:00' }),
    ]);

    expect(errors).toHaveLength(1);
  });

  it('never calls two windows on different days an overlap', () => {
    expect(
      validateRows([
        row({ weekday: 'Monday', opensAt: '09:00', closesAt: '17:00' }),
        row({ weekday: 'Tuesday', opensAt: '09:00', closesAt: '17:00' }),
      ]),
    ).toEqual([]);
  });

  // An end-of-day window runs to 1440, so anything starting after it on the
  // same day necessarily overlaps it.
  it('counts an end-of-day window as covering the rest of the day', () => {
    const errors = validateRows([
      row({ opensAt: '09:00', closesAt: '', closesAtEndOfDay: true }),
      row({ opensAt: '22:00', closesAt: '23:00' }),
    ]);

    expect(errors).toHaveLength(1);
  });

  // Reporting "this overlaps" about a row whose own times are nonsense is noise
  // on top of the real problem.
  it('reports only the row-level problem for a row that is internally invalid', () => {
    const errors = validateRows([
      row({ opensAt: '17:00', closesAt: '09:00' }),
      row({ opensAt: '10:00', closesAt: '11:00' }),
    ]);

    expect(errors).toHaveLength(1);
    expect(errors[0].message).toContain('after the opening time');
  });

  it('accepts an empty schedule — a resource open at no time is a real state', () => {
    expect(validateRows([])).toEqual([]);
  });
});

describe('hasChanges', () => {
  it('sees no change in a schedule that matches what the server sent', () => {
    const saved = [window({ opensAt: '09:00:00', closesAt: '17:00:00' })];

    expect(hasChanges(toRows(saved), saved)).toBe(false);
  });

  it('sees a change when a time moves', () => {
    const saved = [window()];
    const rows = toRows(saved);
    rows[0].closesAt = '18:00';

    expect(hasChanges(rows, saved)).toBe(true);
  });

  it('sees a change when a window is removed, and when one is added', () => {
    const saved = [window()];

    expect(hasChanges([], saved)).toBe(true);
    expect(hasChanges([...toRows(saved), row({ weekday: 'Friday' })], saved)).toBe(true);
  });

  // The endpoint replaces the set, so an identical set is not an edit — a row
  // deleted and re-added the same must not light up the save button.
  it('ignores a row that was removed and recreated identically', () => {
    const saved = [window()];
    const recreated = [row({ weekday: 'Monday', opensAt: '09:00', closesAt: '17:00' })];

    expect(hasChanges(recreated, saved)).toBe(false);
  });

  // Decision `0022` again, from the other direction: the flag and a stored
  // 23:59:59 are the same schedule, so toggling it back must not read as dirty.
  it('treats the end-of-day flag as identical to a stored 23:59:59', () => {
    const saved = [window({ closesAt: END_OF_DAY })];

    expect(hasChanges(toRows(saved), saved)).toBe(false);
  });
});
