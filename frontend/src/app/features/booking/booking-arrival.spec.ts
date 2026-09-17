import { convertToParamMap } from '@angular/router';
import { buildBookingQueryParams, parseBookingSelection } from './booking-arrival';

// Angular's own ParamMap, so these tests exercise the exact type the component
// hands over rather than a hand-rolled stand-in that might behave differently
// for a missing key.
function params(values: Record<string, string>) {
  return convertToParamMap(values);
}

const validParams = {
  startUtc: '2026-09-24T13:15:00Z',
  endUtc: '2026-09-24T15:30:00Z',
  quantity: '1',
};

describe('buildBookingQueryParams', () => {
  it('writes the three parameters the booking screen reads back', () => {
    expect(
      buildBookingQueryParams({
        startUtc: '2026-09-24T13:15:00Z',
        endUtc: '2026-09-24T15:30:00Z',
        quantity: 2,
      }),
    ).toEqual({
      startUtc: '2026-09-24T13:15:00Z',
      endUtc: '2026-09-24T15:30:00Z',
      quantity: '2',
    });
  });

  // The availability grid's own instants come out of toISOString(), which
  // always carries ".000". The API refuses fractional seconds outright, so
  // they are dropped here rather than left for the submit step to notice.
  it('drops the sub-second part the availability grid produces', () => {
    expect(
      buildBookingQueryParams({
        startUtc: '2026-09-21T08:00:00.000Z',
        endUtc: '2026-09-21T09:00:00.000Z',
        quantity: 1,
      }),
    ).toMatchObject({ startUtc: '2026-09-21T08:00:00Z', endUtc: '2026-09-21T09:00:00Z' });
  });

  // Round-tripping is the property that actually matters: whatever the writer
  // emits, the reader accepts unchanged.
  it('round-trips through parseBookingSelection', () => {
    const selection = { startUtc: '2026-09-24T13:15:00Z', endUtc: '2026-09-24T15:30:00Z', quantity: 3 };
    expect(parseBookingSelection(params(buildBookingQueryParams(selection)))).toEqual(selection);
  });
});

describe('parseBookingSelection', () => {
  it('accepts the three parameters the availability screen navigates with', () => {
    expect(parseBookingSelection(params(validParams))).toEqual({
      startUtc: '2026-09-24T13:15:00Z',
      endUtc: '2026-09-24T15:30:00Z',
      quantity: 1,
    });
  });

  it('ignores unrelated query parameters', () => {
    expect(parseBookingSelection(params({ ...validParams, utm_source: 'email' }))?.quantity).toBe(1);
  });

  it('keeps a pooled quantity rather than collapsing it to 1', () => {
    expect(parseBookingSelection(params({ ...validParams, quantity: '4' }))?.quantity).toBe(4);
  });

  // An explicit offset names the same instant as its Z form, so both have to
  // produce an identical selection — the normalization step 3's submit then
  // has nothing left to do.
  it('normalizes an offset instant to whole-second UTC', () => {
    expect(
      parseBookingSelection(
        params({ startUtc: '2026-09-24T15:15:00.499+02:00', endUtc: '2026-09-24T17:30:00+02:00', quantity: '1' }),
      ),
    ).toEqual({ startUtc: '2026-09-24T13:15:00Z', endUtc: '2026-09-24T15:30:00Z', quantity: 1 });
  });

  it.each([
    ['nothing at all', {}],
    ['only a start', { startUtc: validParams.startUtc }],
    ['a missing quantity', { startUtc: validParams.startUtc, endUtc: validParams.endUtc }],
    // Without the zone check this would be read as the *viewer's* local time
    // and silently shift the booking by their offset — the exact class of bug
    // CLAUDE.md §4.3 keeps out of this client, and what
    // CreateBookingCommandRequestValidator.CarryAZone refuses server-side.
    ['an instant with no zone designator', { ...validParams, startUtc: '2026-09-24T13:15:00' }],
    ['an unparseable instant', { ...validParams, startUtc: 'tomorrowZ' }],
    // CK_Bookings_Interval: the end must be strictly after the start.
    ['an inverted interval', { ...validParams, startUtc: validParams.endUtc, endUtc: validParams.startUtc }],
    ['a zero-length interval', { ...validParams, endUtc: validParams.startUtc }],
    // CK_Bookings_Quantity: a positive integer.
    ['a zero quantity', { ...validParams, quantity: '0' }],
    ['a negative quantity', { ...validParams, quantity: '-2' }],
    ['a fractional quantity', { ...validParams, quantity: '1.5' }],
    ['a non-numeric quantity', { ...validParams, quantity: 'two' }],
    // parseInt would take the leading digits and call this a 2.
    ['a quantity with trailing text', { ...validParams, quantity: '2 rooms' }],
  ])('rejects %s', (_label, values) => {
    expect(parseBookingSelection(params(values))).toBeNull();
  });
});
