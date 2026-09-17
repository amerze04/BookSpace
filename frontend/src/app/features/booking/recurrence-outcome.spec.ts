import { HttpErrorResponse } from '@angular/common/http';
import { RecurrenceOccurrenceReport } from './recurrence.models';
import {
  occurrenceReasonLabel,
  parseSeriesRefusal,
  seriesOutcomeFromResponse,
  summarizeOccurrences,
  summaryLine,
} from './recurrence-outcome';

function created(occurrenceDate: string, bookingId = 'b1'): RecurrenceOccurrenceReport {
  return { occurrenceDate, status: 'Created', bookingId, reasonCode: null };
}

function refused(occurrenceDate: string, reasonCode: string): RecurrenceOccurrenceReport {
  return { occurrenceDate, status: 'Refused', bookingId: null, reasonCode };
}

function skipped(occurrenceDate: string): RecurrenceOccurrenceReport {
  return { occurrenceDate, status: 'SkippedSpringForwardGap', bookingId: null, reasonCode: null };
}

// The all-refused 422, exactly as the running API returns it — `occurrences`
// is a top-level key beside `reasonCode`, confirmed live during step 1.
function noOccurrencesCreated(occurrences: RecurrenceOccurrenceReport[]): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 422,
    statusText: 'Unprocessable Content',
    error: {
      title: 'The request was rejected by a rule.',
      status: 422,
      reasonCode: 'NoOccurrencesCreated',
      correlationId: 'c1',
      occurrences,
    },
  });
}

describe('parseSeriesRefusal', () => {
  it('reads the breakdown off an all-refused 422', () => {
    const outcome = parseSeriesRefusal(
      noOccurrencesCreated([refused('2026-09-24', 'OutsideAvailability'), refused('2026-10-01', 'BlackoutPeriod')]),
    );

    // No rule to point at: the handler compensates its own RecurrenceRule away
    // when nothing was created.
    expect(outcome?.recurrenceRuleId).toBeNull();
    expect(outcome?.occurrences).toHaveLength(2);
  });

  it('ignores any other failure, so the reason-code catalogue can handle it', () => {
    expect(
      parseSeriesRefusal(
        new HttpErrorResponse({
          status: 422,
          statusText: 'Error',
          error: { title: 'x', status: 422, reasonCode: 'ResourceArchived', correlationId: 'c1' },
        }),
      ),
    ).toBeNull();

    expect(parseSeriesRefusal(new HttpErrorResponse({ status: 0, statusText: 'Unknown' }))).toBeNull();
    expect(parseSeriesRefusal(new TypeError('boom'))).toBeNull();
  });

  // Defensive: the code without the payload would otherwise render an empty
  // breakdown as though the series had no dates at all.
  it('ignores the code without its occurrences', () => {
    expect(
      parseSeriesRefusal(
        new HttpErrorResponse({
          status: 422,
          statusText: 'Error',
          error: { title: 'x', status: 422, reasonCode: 'NoOccurrencesCreated', correlationId: 'c1' },
        }),
      ),
    ).toBeNull();
  });
});

// The property the whole panel rests on: a 201 and a 422 produce the same
// shape, so one renderer serves both.
describe('a 201 and a 422 agree', () => {
  it('produces the same occurrences either way', () => {
    const occurrences = [created('2026-09-24'), refused('2026-10-01', 'SlotUnavailable')];

    const fromSuccess = seriesOutcomeFromResponse({ recurrenceRuleId: 'rr1', occurrences });
    const fromRefusal = parseSeriesRefusal(noOccurrencesCreated(occurrences));

    expect(fromSuccess.occurrences).toEqual(fromRefusal?.occurrences);
    expect(fromSuccess.recurrenceRuleId).toBe('rr1');
    expect(fromRefusal?.recurrenceRuleId).toBeNull();
  });
});

describe('summarizeOccurrences', () => {
  it('counts each status', () => {
    const summary = summarizeOccurrences([
      created('2026-09-24'),
      created('2026-10-01'),
      skipped('2027-03-14'),
      refused('2026-10-08', 'SlotUnavailable'),
    ]);

    expect(summary).toEqual({ created: 2, skipped: 1, refused: 1, total: 4 });
  });

  it('names only what actually happened', () => {
    expect(summaryLine(summarizeOccurrences([created('2026-09-24'), created('2026-10-01')]))).toBe('2 booked');
    expect(
      summaryLine(summarizeOccurrences([created('2026-09-24'), skipped('2027-03-14'), refused('2026-10-08', 'X')])),
    ).toBe('1 booked, 1 skipped, 1 refused');
    expect(summaryLine(summarizeOccurrences([]))).toBe('No occurrences');
  });

  // FR-7.1: every occurrence of a series on an approval-gated resource is
  // created `Pending`, which holds nothing yet — so "2 booked" would claim two
  // times that are in fact two requests.
  it('calls created occurrences requested when the resource gates on approval', () => {
    const summary = summarizeOccurrences([created('2026-09-24'), created('2026-10-01'), refused('2026-10-08', 'X')]);

    expect(summaryLine(summary, true)).toBe('2 requested, 1 refused');
    expect(summaryLine(summary, false)).toBe('2 booked, 1 refused');
  });
});

describe('occurrenceReasonLabel', () => {
  it('says what happened to a created date', () => {
    expect(occurrenceReasonLabel(created('2026-09-24'))).toBe('Booked');
  });

  // `Created` is the report's word for "a Booking row exists", not for
  // "confirmed" — the handler creates it Pending whenever the resource
  // requires approval.
  it('calls a created date pending when the resource requires approval', () => {
    expect(occurrenceReasonLabel(created('2026-09-24'), true)).toBe('Pending approval');
    expect(occurrenceReasonLabel(created('2026-09-24'), false)).toBe('Booked');
  });

  // Approval changes nothing about a date that was never created.
  it('leaves skips and refusals alone either way', () => {
    expect(occurrenceReasonLabel(refused('2026-10-01', 'SlotUnavailable'), true)).toBe('Already booked');
    expect(occurrenceReasonLabel(skipped('2027-03-14'), true)).toContain('does not exist on that date');
  });

  // Decision `0008`: the local time genuinely does not exist on that date, and
  // the policy is to skip rather than shift — surprising enough to spell out.
  it('explains a spring-forward skip rather than just naming it', () => {
    expect(occurrenceReasonLabel(skipped('2027-03-14'))).toContain('does not exist on that date');
    expect(occurrenceReasonLabel(skipped('2027-03-14'))).toContain('clocks move forward');
  });

  it.each([
    ['SlotUnavailable', 'Already booked'],
    ['CapacityExceeded', 'Not enough units free'],
    ['BlackoutPeriod', 'Blackout period'],
    ['OutsideAvailability', 'Outside bookable hours'],
    ['BookingInThePast', 'Already in the past'],
  ])('words %s for a single date', (reasonCode, expected) => {
    expect(occurrenceReasonLabel(refused('2026-10-01', reasonCode))).toBe(expected);
  });

  // An unknown code still says *something* specific rather than being
  // swallowed — the same instinct the one-off catalogue's fallback follows.
  it('falls back to the code itself', () => {
    expect(occurrenceReasonLabel(refused('2026-10-01', 'SomeFutureCode'))).toBe('SomeFutureCode');
  });
});
