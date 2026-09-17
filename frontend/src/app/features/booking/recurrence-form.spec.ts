import {
  MAX_RECURRENCE_SPAN_YEARS,
  RecurrenceFormValue,
  buildRecurrenceRequest,
  defaultRecurrenceFor,
  impliedEndDate,
  isOccurrenceCountWithinMaxSpan,
  isWithinMaxSpan,
  recurrenceFromSelection,
  validateRecurrenceForm,
} from './recurrence-form';

// Open 09:00-17:00 every weekday, so the window guard is satisfied by the
// fixture's own 09:15-11:30 and the tests below exercise one rule at a time.
const WEEKDAY_WINDOWS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'].map((weekday) => ({
  weekday,
  opensAt: '09:00:00',
  closesAt: '17:00:00',
}));

const NO_LIMITS = {
  minDurationMinutes: null,
  maxDurationMinutes: null,
  availabilityWindows: WEEKDAY_WINDOWS,
};

// 2026-09-24 is a Thursday.
function form(overrides: Partial<RecurrenceFormValue> = {}): RecurrenceFormValue {
  return {
    frequency: 'Weekly',
    intervalValue: 1,
    localStartTime: '09:15',
    localEndTime: '11:30',
    startDate: '2026-09-24',
    endCondition: 'occurrenceCount',
    endDate: '',
    occurrenceCount: 4,
    ...overrides,
  };
}

describe('validateRecurrenceForm', () => {
  it('accepts an ordinary weekly series', () => {
    expect(validateRecurrenceForm(form(), NO_LIMITS)).toEqual({});
  });

  describe('the interval', () => {
    it.each([0, -1, 1.5, NaN])('refuses %s', (intervalValue) => {
      expect(validateRecurrenceForm(form({ intervalValue }), NO_LIMITS).interval).toBeDefined();
    });
  });

  describe('the times', () => {
    it('refuses an end at or before the start', () => {
      expect(validateRecurrenceForm(form({ localEndTime: '09:15' }), NO_LIMITS).times).toBeDefined();
      expect(validateRecurrenceForm(form({ localEndTime: '08:00' }), NO_LIMITS).times).toBeDefined();
    });

    // Every occurrence shares the series' nominal length, which the handler
    // checks once against the resource — so the form checks the same thing
    // rather than letting a 422 be the first to say it.
    it('refuses a length the resource does not allow', () => {
      const tooShort = validateRecurrenceForm(form(), { ...NO_LIMITS, minDurationMinutes: 180 });
      expect(tooShort.duration).toContain('at least 180 minutes');

      const tooLong = validateRecurrenceForm(form(), { ...NO_LIMITS, maxDurationMinutes: 60 });
      expect(tooLong.duration).toContain('at most 60 minutes');
    });

    // `null` means no rule configured, never a default — the same trap the
    // 2026-09-16 hardening pass fixed on the availability screen.
    it('invents no limits when the resource configures none', () => {
      expect(validateRecurrenceForm(form({ localStartTime: '09:00', localEndTime: '09:05' }), NO_LIMITS)).toEqual({});
    });
  });

  describe('the end condition', () => {
    it('refuses an end date before the start date', () => {
      const errors = validateRecurrenceForm(
        form({ endCondition: 'endDate', endDate: '2026-09-01' }),
        NO_LIMITS,
      );
      expect(errors.endDate).toContain('must not be before');
    });

    it('refuses an empty end date', () => {
      expect(
        validateRecurrenceForm(form({ endCondition: 'endDate', endDate: '' }), NO_LIMITS).endDate,
      ).toBeDefined();
    });

    it.each([0, -3, 2.5, NaN])('refuses an occurrence count of %s', (occurrenceCount) => {
      expect(
        validateRecurrenceForm(form({ occurrenceCount }), NO_LIMITS).occurrenceCount,
      ).toBeDefined();
    });

    // Only the selected arm is judged: the other one's value is whatever the
    // form last held and is not sent.
    it('ignores the arm that is not selected', () => {
      expect(validateRecurrenceForm(form({ endDate: '1999-01-01' }), NO_LIMITS)).toEqual({});
      expect(
        validateRecurrenceForm(
          form({ endCondition: 'endDate', endDate: '2026-10-29', occurrenceCount: 0 }),
          NO_LIMITS,
        ),
      ).toEqual({});
    });
  });

  // The guard that lets the times stay editable at all: a series whose time of
  // day falls outside the resource's hours has *every* occurrence refused, and
  // the windows are already loaded here, so saying so costs nothing.
  describe('opening hours', () => {
    it('refuses a weekly series at a time the resource is closed on that weekday', () => {
      const errors = validateRecurrenceForm(
        form({ localStartTime: '07:00', localEndTime: '08:00' }),
        NO_LIMITS,
      );
      expect(errors.times).toContain('Thursday');
    });

    // 2026-09-26 is a Saturday, and the fixture opens Monday-Friday only.
    it('refuses a weekly series starting on a day with no hours at all', () => {
      expect(validateRecurrenceForm(form({ startDate: '2026-09-26' }), NO_LIMITS).times).toContain('Saturday');
    });

    // "Partly open is not open" — a booking running past closing is refused,
    // never truncated (ReasonCodes.OutsideAvailability).
    it('refuses a span that only partly overlaps the window', () => {
      expect(
        validateRecurrenceForm(form({ localStartTime: '08:00', localEndTime: '10:00' }), NO_LIMITS).times,
      ).toBeDefined();
      expect(
        validateRecurrenceForm(form({ localStartTime: '16:00', localEndTime: '18:00' }), NO_LIMITS).times,
      ).toBeDefined();
    });

    // Daily and Monthly land on varying weekdays, so *some* refusals are
    // legitimate and belong to the per-occurrence report — only a time that
    // fits no weekday at all is hopeless.
    it('accepts a daily series whose time fits at least one weekday', () => {
      expect(
        validateRecurrenceForm(form({ frequency: 'Daily', startDate: '2026-09-26' }), NO_LIMITS),
      ).toEqual({});
    });

    it('refuses a daily series whose time fits no weekday', () => {
      const errors = validateRecurrenceForm(
        form({ frequency: 'Daily', localStartTime: '05:00', localEndTime: '06:00' }),
        NO_LIMITS,
      );
      expect(errors.times).toContain('any day');
    });

    it('says nothing when the resource publishes no windows', () => {
      expect(
        validateRecurrenceForm(form({ localStartTime: '07:00', localEndTime: '08:00' }), {
          ...NO_LIMITS,
          availabilityWindows: [],
        }),
      ).toEqual({});
    });
  });

  // Decision `0007`'s two-year cap, at and past the boundary for each
  // frequency — the same arithmetic the server uses, so the two cannot
  // disagree about which series fit.
  describe('the two-year span cap', () => {
    it('accepts an end date exactly two years out and refuses one day more', () => {
      expect(
        validateRecurrenceForm(form({ endCondition: 'endDate', endDate: '2028-09-24' }), NO_LIMITS),
      ).toEqual({});

      expect(
        validateRecurrenceForm(form({ endCondition: 'endDate', endDate: '2028-09-25' }), NO_LIMITS).endDate,
      ).toContain(`${MAX_RECURRENCE_SPAN_YEARS} years`);
    });

    it('accepts a daily series landing exactly on the cap and refuses the next one', () => {
      // 2026-09-24 to 2028-09-24 is 731 days (2028 is a leap year), and the
      // last occurrence is count-1 steps out — so 732 occurrences land exactly
      // on the cap and 733 is the first past it.
      expect(validateRecurrenceForm(form({ frequency: 'Daily', occurrenceCount: 732 }), NO_LIMITS)).toEqual({});
      expect(
        validateRecurrenceForm(form({ frequency: 'Daily', occurrenceCount: 733 }), NO_LIMITS).occurrenceCount,
      ).toBeDefined();
    });

    it('counts weekly steps as weeks, not days', () => {
      // 104 weekly occurrences = 103 steps = 721 days, inside two years; 106
      // would be 735 days, past it.
      expect(validateRecurrenceForm(form({ occurrenceCount: 104 }), NO_LIMITS)).toEqual({});
      expect(validateRecurrenceForm(form({ occurrenceCount: 106 }), NO_LIMITS).occurrenceCount).toBeDefined();
    });

    it('counts monthly steps as months', () => {
      expect(validateRecurrenceForm(form({ frequency: 'Monthly', occurrenceCount: 25 }), NO_LIMITS)).toEqual({});
      expect(
        validateRecurrenceForm(form({ frequency: 'Monthly', occurrenceCount: 26 }), NO_LIMITS).occurrenceCount,
      ).toBeDefined();
    });

    // The bug item 12 of the 2026-09-15 hardening pass fixed server-side: a
    // large interval with few occurrences is legal, and was being refused by
    // a per-field proxy for the cap. The client must not reintroduce it.
    it('accepts a large interval with few occurrences', () => {
      expect(
        validateRecurrenceForm(form({ frequency: 'Daily', intervalValue: 400, occurrenceCount: 2 }), NO_LIMITS),
      ).toEqual({});
    });
  });
});

describe('impliedEndDate', () => {
  it('steps by days, weeks and months', () => {
    expect(impliedEndDate('Daily', 1, '2026-09-24', 5)).toBe('2026-09-28');
    expect(impliedEndDate('Weekly', 2, '2026-09-24', 3)).toBe('2026-10-22');
    expect(impliedEndDate('Monthly', 1, '2026-09-24', 4)).toBe('2026-12-24');
  });

  it('is the start date itself for a single occurrence', () => {
    expect(impliedEndDate('Weekly', 1, '2026-09-24', 1)).toBe('2026-09-24');
  });

  // DateOnly.AddMonths clamps to the last day of the target month; JS's own
  // Date rolls over into the next one. The client has to match the server.
  it('clamps a monthly step onto a shorter month, as DateOnly does', () => {
    expect(impliedEndDate('Monthly', 1, '2026-01-31', 2)).toBe('2026-02-28');
    expect(impliedEndDate('Monthly', 1, '2026-08-31', 2)).toBe('2026-09-30');
  });
});

describe('isWithinMaxSpan / isOccurrenceCountWithinMaxSpan', () => {
  it('treats the boundary itself as inside', () => {
    expect(isWithinMaxSpan('2026-09-24', '2028-09-24')).toBe(true);
    expect(isWithinMaxSpan('2026-09-24', '2028-09-25')).toBe(false);
  });

  it('handles a leap-day start the way DateOnly.AddYears does', () => {
    // 2028-02-29 + 2 years clamps to 2030-02-28.
    expect(isWithinMaxSpan('2028-02-29', '2030-02-28')).toBe(true);
    expect(isWithinMaxSpan('2028-02-29', '2030-03-01')).toBe(false);
  });

  it('judges a count by what it implies, not by the count itself', () => {
    expect(isOccurrenceCountWithinMaxSpan('Daily', 400, '2026-09-24', 2)).toBe(true);
    expect(isOccurrenceCountWithinMaxSpan('Daily', 400, '2026-09-24', 3)).toBe(false);
  });
});

// The form a member gets when they come straight to the recurring half
// without picking a slot — the entry point that keeps the availability screen
// from being a toll booth.
describe('defaultRecurrenceFor', () => {
  it('starts on the first upcoming day the resource is actually open', () => {
    // 2026-09-19 is a Saturday; the fixture opens Monday-Friday.
    const value = defaultRecurrenceFor({ ...NO_LIMITS, minDurationMinutes: 30 }, '2026-09-19');

    expect(value.startDate).toBe('2026-09-21');
    expect(value.localStartTime).toBe('09:00');
    expect(value.localEndTime).toBe('09:30');
  });

  it('starts today when today is already open', () => {
    expect(defaultRecurrenceFor(NO_LIMITS, '2026-09-24').startDate).toBe('2026-09-24');
  });

  it('falls back to an hour when the resource sets no minimum', () => {
    expect(defaultRecurrenceFor(NO_LIMITS, '2026-09-24').localEndTime).toBe('10:00');
  });

  it('never runs past the day\'s own closing time', () => {
    const value = defaultRecurrenceFor(
      {
        minDurationMinutes: 600,
        maxDurationMinutes: null,
        availabilityWindows: [{ weekday: 'Thursday', opensAt: '09:00:00', closesAt: '13:00:00' }],
      },
      '2026-09-24',
    );

    expect(value.localEndTime).toBe('13:00');
  });

  // Nothing to search for, and the form's own guards say the rest.
  it('falls back to the given day when the resource has no windows', () => {
    const value = defaultRecurrenceFor({ ...NO_LIMITS, availabilityWindows: [] }, '2026-09-19');
    expect(value.startDate).toBe('2026-09-19');
  });

  it('opens on a valid form', () => {
    const schedule = { ...NO_LIMITS, minDurationMinutes: 30 };
    expect(validateRecurrenceForm(defaultRecurrenceFor(schedule, '2026-09-19'), schedule)).toEqual({});
  });
});

describe('recurrenceFromSelection', () => {
  it('keeps the picked slot\'s own day and times', () => {
    const value = recurrenceFromSelection('2026-09-24', 9 * 60 + 15, 11 * 60 + 30);

    expect(value).toMatchObject({
      startDate: '2026-09-24',
      localStartTime: '09:15',
      localEndTime: '11:30',
      frequency: 'Weekly',
      endCondition: 'occurrenceCount',
    });
  });
});

describe('buildRecurrenceRequest', () => {
  it('sends local wall-clock times as "HH:mm:ss"', () => {
    const request = buildRecurrenceRequest(form(), 'r1', 1, 'Standup');

    expect(request).toMatchObject({
      resourceId: 'r1',
      frequency: 'Weekly',
      intervalValue: 1,
      localStartTime: '09:15:00',
      localEndTime: '11:30:00',
      startDate: '2026-09-24',
      quantity: 1,
      title: 'Standup',
    });
    // No timeZoneId: decision `0003` puts the series in the resource's own
    // zone, so there is nothing here for a client to name.
    expect(Object.keys(request)).not.toContain('timeZoneId');
  });

  it('sends occurrenceCount alone when that arm is selected', () => {
    const wire = JSON.parse(JSON.stringify(buildRecurrenceRequest(form(), 'r1', 1, null))) as Record<string, unknown>;

    expect(wire['occurrenceCount']).toBe(4);
    expect('endDate' in wire).toBe(false);
  });

  it('sends endDate alone when that arm is selected', () => {
    const value = form({ endCondition: 'endDate', endDate: '2026-12-24' });
    const wire = JSON.parse(JSON.stringify(buildRecurrenceRequest(value, 'r1', 2, null))) as Record<string, unknown>;

    expect(wire['endDate']).toBe('2026-12-24');
    expect('occurrenceCount' in wire).toBe(false);
    expect(wire['quantity']).toBe(2);
  });
});
