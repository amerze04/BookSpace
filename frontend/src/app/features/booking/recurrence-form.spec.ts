import {
  MAX_RECURRENCE_SPAN_YEARS,
  MAX_START_DATE,
  RecurrenceFormValue,
  ResourceSchedule,
  buildRecurrenceRequest,
  defaultRecurrenceFor,
  impliedEndDate,
  isOccurrenceCountWithinMaxSpan,
  isValidLocalDate,
  isWithinMaxSpan,
  recurrenceFromSelection,
  recurrenceUnavailableReason,
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

// Ordinary things a member does to a form, none of which may reach `Date`
// arithmetic: `addDays('')` and `addDays(d, 7e18)` both throw `RangeError:
// Invalid time value` rather than returning something odd, and this function
// runs inside a `computed` the template reads on every keystroke. Every case
// below threw before the 2026-09-17 pass; the assertion that matters as much
// as the message is that none of them throws now.
describe('validateRecurrenceForm on input the form cannot stop', () => {
  function errorsFor(overrides: Partial<RecurrenceFormValue>) {
    return validateRecurrenceForm(form(overrides), NO_LIMITS);
  }

  it('reports a cleared start date instead of throwing', () => {
    expect(() => errorsFor({ startDate: '' })).not.toThrow();
    expect(errorsFor({ startDate: '' }).startDate).toContain('Choose the date');
  });

  // Weekly and Daily step in days, Monthly in months — three different code
  // paths into the same helpers.
  it.each(['Daily', 'Weekly', 'Monthly'] as const)(
    'survives a cleared start date on a %s series',
    (frequency) => {
      expect(() => errorsFor({ frequency, startDate: '' })).not.toThrow();
    },
  );

  it('reports a cleared start date on the end-date arm too', () => {
    // '2026-10-01' < '' is false, so before this pass an empty start date left
    // *no* error at all on this arm and the form submitted a request the
    // server could only answer with a 400.
    const errors = errorsFor({ startDate: '', endCondition: 'endDate', endDate: '2026-10-01' });

    expect(errors.startDate).toBeDefined();
  });

  it.each(['2026-02-31', '2026-13-01', '26-09-24', 'not-a-date'])(
    'refuses the malformed start date %s',
    (startDate) => {
      expect(() => errorsFor({ startDate })).not.toThrow();
      expect(errorsFor({ startDate }).startDate).toContain('valid start date');
    },
  );

  it('refuses a malformed end date', () => {
    expect(errorsFor({ endCondition: 'endDate', endDate: '2026-02-30' }).endDate).toContain('valid end date');
  });

  // The server's own MaxStartDate, restated: past it *its* span checks are
  // what overflow, so it refuses the start date outright.
  it('refuses a start date past what the server will accept', () => {
    expect(errorsFor({ startDate: '9999-01-01' }).startDate).toContain(MAX_START_DATE);
  });

  // 1e21 passes Number.isInteger — the check this used to make — and its
  // product with a count is then multiplied into a Date.
  it.each([1e21, Number.MAX_SAFE_INTEGER + 2, Infinity, NaN])(
    'refuses the interval %s without computing anything from it',
    (intervalValue) => {
      expect(() => errorsFor({ intervalValue })).not.toThrow();
      expect(errorsFor({ intervalValue }).interval).toBeDefined();
    },
  );

  it.each([1e21, Number.MAX_SAFE_INTEGER + 2, Infinity, NaN])(
    'refuses the occurrence count %s without computing anything from it',
    (occurrenceCount) => {
      expect(() => errorsFor({ occurrenceCount })).not.toThrow();
      expect(errorsFor({ occurrenceCount }).occurrenceCount).toBeDefined();
    },
  );

  // Each operand is a safe integer; the product is not, which is the case
  // `Number.isInteger` on the operands alone would wave through.
  it('refuses an interval and count whose product is not a safe integer', () => {
    const errors = errorsFor({ intervalValue: 1e9, occurrenceCount: 1e9 });

    expect(errors.occurrenceCount).toBeDefined();
  });

  it('refuses a large-but-safe pair by the span it implies, not by either field', () => {
    expect(errorsFor({ intervalValue: 1_000_000, occurrenceCount: 2 }).occurrenceCount).toBeDefined();
    // ...while the legal large-interval case item 12 fixed server-side still
    // passes, which is the whole reason the bound is on the implied span.
    expect(errorsFor({ frequency: 'Daily', intervalValue: 400, occurrenceCount: 2 }).occurrenceCount).toBeUndefined();
  });

  it('never throws for any combination of a cleared date and an extreme number', () => {
    for (const startDate of ['', '2026-09-24', 'nonsense']) {
      for (const intervalValue of [NaN, 1, 1e21]) {
        for (const occurrenceCount of [NaN, 4, Number.MAX_SAFE_INTEGER]) {
          for (const endCondition of ['endDate', 'occurrenceCount'] as const) {
            expect(() =>
              errorsFor({ startDate, intervalValue, occurrenceCount, endCondition, endDate: '2027-01-01' }),
            ).not.toThrow();
          }
        }
      }
    }
  });
});

describe('isValidLocalDate', () => {
  it('accepts a real calendar date', () => {
    expect(isValidLocalDate('2026-09-24')).toBe(true);
    expect(isValidLocalDate('2028-02-29')).toBe(true);
  });

  // `new Date('2026-02-31')` rolls into March and `new Date('26-09-24')` reads
  // the year as 1926 — which is why this is digits, not a Date round trip.
  it.each(['', '2026-02-31', '2026-09-31', '2027-02-29', '26-09-24', '2026-9-24', '0000-01-01'])(
    'refuses %s',
    (value) => {
      expect(isValidLocalDate(value)).toBe(false);
    },
  );
});

// The form a member gets when they come straight to the recurring half
// without picking a slot — the entry point that keeps the availability screen
// from being a toll booth.
describe('defaultRecurrenceFor', () => {
  function at(date: string, hours: number, minutes = 0) {
    return { date, minutesOfDay: hours * 60 + minutes };
  }

  it('starts on the first upcoming day the resource is actually open', () => {
    // 2026-09-19 is a Saturday; the fixture opens Monday-Friday.
    const value = defaultRecurrenceFor({ ...NO_LIMITS, minDurationMinutes: 30 }, at('2026-09-19', 8));

    expect(value).toMatchObject({
      startDate: '2026-09-21',
      localStartTime: '09:00',
      localEndTime: '09:30',
    });
  });

  it('starts today when today is open and has not opened yet', () => {
    expect(defaultRecurrenceFor(NO_LIMITS, at('2026-09-24', 7))?.startDate).toBe('2026-09-24');
  });

  it('falls back to an hour when the resource sets no minimum', () => {
    expect(defaultRecurrenceFor(NO_LIMITS, at('2026-09-24', 7))?.localEndTime).toBe('10:00');
  });

  // The bug: the resource's local *date* was all this took, so at 15:30 in the
  // resource's own zone it proposed this morning's 09:00-10:00 — a first
  // occurrence already in the past, refused with BookingInThePast after a
  // round trip.
  it('starts from the current time, not the opening time, once the day is under way', () => {
    const value = defaultRecurrenceFor(NO_LIMITS, at('2026-09-24', 15, 30));

    expect(value).toMatchObject({
      startDate: '2026-09-24',
      localStartTime: '15:30',
      localEndTime: '16:30',
    });
  });

  it('rounds the start up to the next quarter hour rather than to the minute the page loaded', () => {
    expect(defaultRecurrenceFor(NO_LIMITS, at('2026-09-24', 15, 37))?.localStartTime).toBe('15:45');
  });

  // Today's window has closed; the fixture opens Mon-Fri, so tomorrow does.
  it('moves to the next open day when today\'s hours have passed', () => {
    const value = defaultRecurrenceFor(NO_LIMITS, at('2026-09-24', 18));

    expect(value?.startDate).toBe('2026-09-25');
    expect(value?.localStartTime).toBe('09:00');
  });

  // Eight days searched, not seven: a weekday only comes round again on the
  // eighth, and this resource has exactly one open weekday.
  it('reaches the same weekday next week when that is the only open day', () => {
    const value = defaultRecurrenceFor(
      {
        ...NO_LIMITS,
        availabilityWindows: [{ weekday: 'Thursday', opensAt: '09:00:00', closesAt: '12:00:00' }],
      },
      at('2026-09-24', 13),
    );

    expect(value?.startDate).toBe('2026-10-01');
  });

  // The fallback hour does not fit in the quarter of an hour that is left, so
  // it shrinks to the quarter rather than proposing a series that runs past
  // closing on every occurrence.
  it('shortens to what is left of the window rather than running past closing', () => {
    const value = defaultRecurrenceFor(NO_LIMITS, at('2026-09-24', 16, 45));

    expect(value).toMatchObject({ localStartTime: '16:45', localEndTime: '17:00' });
  });

  // The 60-minute fallback is a preference, not a rule the resource agreed to.
  it('honours a maximum shorter than the fallback hour', () => {
    const value = defaultRecurrenceFor({ ...NO_LIMITS, maxDurationMinutes: 30 }, at('2026-09-24', 7));

    expect(value?.localEndTime).toBe('09:30');
  });

  it('honours a minimum longer than the fallback hour', () => {
    const value = defaultRecurrenceFor({ ...NO_LIMITS, minDurationMinutes: 120 }, at('2026-09-24', 7));

    expect(value?.localEndTime).toBe('11:00');
  });

  // Nothing valid to propose. The caller shows `recurrenceUnavailableReason`'s
  // own explanation rather than a form seeded with something that fails its
  // own guards.
  it('produces nothing when the resource has no windows', () => {
    expect(defaultRecurrenceFor({ ...NO_LIMITS, availabilityWindows: [] }, at('2026-09-19', 8))).toBeNull();
  });

  it('produces nothing when no window is long enough for the minimum', () => {
    expect(defaultRecurrenceFor({ ...NO_LIMITS, minDurationMinutes: 600 }, at('2026-09-21', 8))).toBeNull();
  });

  it('produces nothing when the minimum is longer than the maximum', () => {
    expect(
      defaultRecurrenceFor(
        { ...NO_LIMITS, minDurationMinutes: 120, maxDurationMinutes: 60 },
        at('2026-09-21', 8),
      ),
    ).toBeNull();
  });

  // The property that matters more than any single case above: whatever it
  // proposes has to pass the guards the form is about to apply to it.
  it('never opens on a form that fails its own validation', () => {
    const schedules: ResourceSchedule[] = [
      NO_LIMITS,
      { ...NO_LIMITS, minDurationMinutes: 30 },
      { ...NO_LIMITS, minDurationMinutes: 45, maxDurationMinutes: 90 },
      { ...NO_LIMITS, maxDurationMinutes: 20 },
      { ...NO_LIMITS, availabilityWindows: [{ weekday: 'Thursday', opensAt: '09:00:00', closesAt: '10:00:00' }] },
    ];

    for (const schedule of schedules) {
      for (const minutesOfDay of [0, 8 * 60, 9 * 60 + 40, 16 * 60 + 50, 23 * 60 + 59]) {
        const value = defaultRecurrenceFor(schedule, { date: '2026-09-24', minutesOfDay });
        if (value !== null) {
          expect(validateRecurrenceForm(value, schedule)).toEqual({});
        }
      }
    }
  });

  // The two answers have to agree: a default of null must always come with a
  // reason the screen can show, and a reason of null must always come with a
  // default.
  it('produces a default exactly when no unavailable reason applies', () => {
    const schedules: ResourceSchedule[] = [
      NO_LIMITS,
      { ...NO_LIMITS, availabilityWindows: [] },
      { ...NO_LIMITS, minDurationMinutes: 600 },
      { ...NO_LIMITS, minDurationMinutes: 120, maxDurationMinutes: 60 },
      { ...NO_LIMITS, availabilityWindows: [{ weekday: 'Thursday', opensAt: '09:00:00', closesAt: '12:00:00' }] },
    ];

    for (const schedule of schedules) {
      for (const minutesOfDay of [0, 13 * 60, 23 * 60]) {
        const hasDefault = defaultRecurrenceFor(schedule, { date: '2026-09-24', minutesOfDay }) !== null;
        expect(hasDefault).toBe(recurrenceUnavailableReason(schedule) === null);
      }
    }
  });
});

// Finding 6: an active resource with no published hours produced a form that
// looked submittable and whose every occurrence was guaranteed to be refused
// with OutsideAvailability — because `outsideOpeningHours` deliberately says
// nothing when there are no windows to judge against.
describe('recurrenceUnavailableReason', () => {
  it('says nothing for a resource that can be booked', () => {
    expect(recurrenceUnavailableReason(NO_LIMITS)).toBeNull();
    expect(recurrenceUnavailableReason({ ...NO_LIMITS, minDurationMinutes: 480 })).toBeNull();
  });

  it('names an unpublished schedule', () => {
    expect(recurrenceUnavailableReason({ ...NO_LIMITS, availabilityWindows: [] })).toBe('noOpeningHours');
  });

  it('names duration limits that contradict each other', () => {
    expect(
      recurrenceUnavailableReason({ ...NO_LIMITS, minDurationMinutes: 120, maxDurationMinutes: 60 }),
    ).toBe('durationLimitsConflict');
  });

  it('names hours too short for the shortest booking allowed', () => {
    expect(recurrenceUnavailableReason({ ...NO_LIMITS, minDurationMinutes: 481 })).toBe('noBookableWindow');
  });

  // The distinction the copy must not blur: "no hours configured" is a
  // configuration fact, "nothing free" is an answer only the server has.
  // Nothing here consults bookings or blackouts, and nothing should.
  it('invents no minimum when the resource configures none', () => {
    expect(
      recurrenceUnavailableReason({
        ...NO_LIMITS,
        availabilityWindows: [{ weekday: 'Monday', opensAt: '09:00:00', closesAt: '09:05:00' }],
      }),
    ).toBeNull();
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
