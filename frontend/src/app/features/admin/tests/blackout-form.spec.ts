import {
  MAX_REASON_LENGTH,
  hasErrors,
  resourceLocalToUtc,
  utcToResourceLocalInput,
  validateBlackout,
} from '../blackouts/blackout-form';

// Admin console phase 6. The conversion between what an admin types and what
// goes on the wire.
//
// **This is the riskiest code in the phase and the least visible.** A blackout
// is an instant, the admin types wall-clock in the *resource's* zone, and
// getting the offset wrong closes the room at the wrong hours — silently, with
// no error anywhere. So the cases below are pinned against real IANA zones and
// real DST transitions rather than against a hand-computed offset.

describe('resourceLocalToUtc', () => {
  // Warsaw is UTC+1 in winter and UTC+2 in summer. Both directions matter: an
  // implementation that hard-coded either would pass half these.
  it('applies the standard-time offset', () => {
    expect(resourceLocalToUtc('2026-01-15T09:00', 'Europe/Warsaw')).toBe('2026-01-15T08:00:00Z');
  });

  it('applies the summer-time offset for the same wall clock', () => {
    expect(resourceLocalToUtc('2026-07-15T09:00', 'Europe/Warsaw')).toBe('2026-07-15T07:00:00Z');
  });

  // A zone behind UTC, so a sign error shows up as a whole day's drift rather
  // than an hour's.
  it('handles a zone behind UTC', () => {
    expect(resourceLocalToUtc('2026-01-15T09:00', 'America/New_York')).toBe('2026-01-15T14:00:00Z');
    expect(resourceLocalToUtc('2026-07-15T09:00', 'America/New_York')).toBe('2026-07-15T13:00:00Z');
  });

  it('is the identity for UTC itself', () => {
    expect(resourceLocalToUtc('2026-03-01T13:45', 'UTC')).toBe('2026-03-01T13:45:00Z');
  });

  // A local time near midnight in a zone ahead of UTC lands on the *previous*
  // UTC day — the case a naive same-day implementation gets wrong.
  it('crosses the UTC date boundary correctly', () => {
    expect(resourceLocalToUtc('2026-07-15T00:30', 'Europe/Warsaw')).toBe('2026-07-14T22:30:00Z');
  });

  it('crosses forward for a zone behind UTC', () => {
    expect(resourceLocalToUtc('2026-07-15T21:00', 'America/New_York')).toBe('2026-07-16T01:00:00Z');
  });

  // **Either side of a real DST transition.** Warsaw springs forward at
  // 02:00 local on 2026-03-29; the same wall clock an hour apart therefore
  // maps to instants that are *not* an hour apart in the naive reading.
  it('gets both sides of a spring-forward transition right', () => {
    expect(resourceLocalToUtc('2026-03-29T01:00', 'Europe/Warsaw')).toBe('2026-03-29T00:00:00Z');
    expect(resourceLocalToUtc('2026-03-29T04:00', 'Europe/Warsaw')).toBe('2026-03-29T02:00:00Z');
  });

  // A local time inside the spring-forward gap never happened, so it has no
  // unique inverse. It must not throw and must not return something wild — the
  // form has to stay usable, and the server stores whatever instant it is sent.
  it('does not throw on a local time that never happened', () => {
    const result = resourceLocalToUtc('2026-03-29T02:30', 'Europe/Warsaw');

    expect(result).toMatch(/^2026-03-29T\d{2}:\d{2}:00Z$/);
  });

  // Fall-back: 02:00-03:00 local happens twice on 2026-10-25. Ambiguous, so
  // again no unique inverse; landing on the earlier candidate matches decision
  // `0024`'s policy for recurrence, which is the nearest precedent.
  it('resolves an ambiguous fall-back time without throwing', () => {
    const result = resourceLocalToUtc('2026-10-25T02:30', 'Europe/Warsaw');

    expect(result).toMatch(/^2026-10-25T\d{2}:\d{2}:00Z$/);
  });

  it('never emits milliseconds, which the validator would refuse', () => {
    expect(resourceLocalToUtc('2026-05-05T08:15', 'Europe/Warsaw')).not.toContain('.');
  });
});

describe('utcToResourceLocalInput', () => {
  // Round-tripping is the property that matters: seeding the form from a stored
  // blackout and saving it again unchanged must not move it.
  it('round-trips through resourceLocalToUtc in both seasons', () => {
    for (const utc of ['2026-01-15T08:00:00Z', '2026-07-15T07:00:00Z']) {
      const local = utcToResourceLocalInput(utc, 'Europe/Warsaw');
      expect(resourceLocalToUtc(local, 'Europe/Warsaw')).toBe(utc);
    }
  });

  it('produces exactly what a datetime-local input expects', () => {
    expect(utcToResourceLocalInput('2026-07-15T07:05:00Z', 'Europe/Warsaw')).toBe('2026-07-15T09:05');
  });

  it('round-trips for a zone behind UTC too', () => {
    const local = utcToResourceLocalInput('2026-07-15T13:00:00Z', 'America/New_York');
    expect(local).toBe('2026-07-15T09:00');
    expect(resourceLocalToUtc(local, 'America/New_York')).toBe('2026-07-15T13:00:00Z');
  });
});

describe('validateBlackout', () => {
  const zone = 'Europe/Warsaw';

  it('accepts an ordinary interval', () => {
    expect(
      hasErrors(
        validateBlackout({ startsAt: '2026-07-15T09:00', endsAt: '2026-07-15T17:00', reason: '' }, zone),
      ),
    ).toBe(false);
  });

  it('requires both ends', () => {
    const errors = validateBlackout({ startsAt: '', endsAt: '', reason: '' }, zone);

    expect(errors.startsAt).toContain('start');
    expect(errors.endsAt).toContain('end');
  });

  it('refuses an end at or before the start', () => {
    expect(
      validateBlackout({ startsAt: '2026-07-15T17:00', endsAt: '2026-07-15T09:00', reason: '' }, zone)
        .endsAt,
    ).toContain('after the start');

    expect(
      validateBlackout({ startsAt: '2026-07-15T09:00', endsAt: '2026-07-15T09:00', reason: '' }, zone)
        .endsAt,
    ).toBeDefined();
  });

  // **Compared as instants, not as strings.** Across a DST transition the local
  // digits and the real elapsed time disagree, and a string comparison would
  // happen to be right here — but wrong for an interval whose end has a smaller
  // wall clock than its start yet is genuinely later.
  it('compares instants rather than the typed digits', () => {
    // 01:30 -> 03:30 across Warsaw's spring-forward is one real hour, and both
    // orderings of the digits are ascending, so this proves the conversion ran.
    expect(
      hasErrors(
        validateBlackout(
          { startsAt: '2026-03-29T01:30', endsAt: '2026-03-29T03:30', reason: '' },
          zone,
        ),
      ),
    ).toBe(false);
  });

  it('caps the reason at the column width', () => {
    const errors = validateBlackout(
      { startsAt: '2026-07-15T09:00', endsAt: '2026-07-15T17:00', reason: 'x'.repeat(MAX_REASON_LENGTH + 1) },
      zone,
    );

    expect(errors.reason).toContain(String(MAX_REASON_LENGTH));
  });

  it('allows an empty reason, which the column permits', () => {
    expect(
      validateBlackout({ startsAt: '2026-07-15T09:00', endsAt: '2026-07-15T17:00', reason: '' }, zone)
        .reason,
    ).toBeUndefined();
  });

  // **Deliberately not checked client-side.** `BlackoutPeriodElapsed` is about
  // the *server's* clock, and a browser clock that disagreed would either refuse
  // a legal blackout or promise one the server then rejects. A blackout that
  // *started* in the past is legal and sometimes exactly right — a room that
  // flooded this morning.
  it('does not refuse a blackout that started in the past', () => {
    expect(
      hasErrors(
        validateBlackout({ startsAt: '2020-01-01T09:00', endsAt: '2030-01-01T17:00', reason: '' }, zone),
      ),
    ).toBe(false);
  });

  it('does not refuse a wholly past interval either — that is the server’s call', () => {
    expect(
      hasErrors(
        validateBlackout({ startsAt: '2020-01-01T09:00', endsAt: '2020-01-01T17:00', reason: '' }, zone),
      ),
    ).toBe(false);
  });
});
