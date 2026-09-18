import { HttpErrorResponse } from '@angular/common/http';
import { describeRecurrenceRejection } from '../rejection/recurrence-rejection';
import { describeBookingRejection } from '../rejection/booking-rejection';

// `POST /recurrence-rules` refused, read in the recurring form's own
// vocabulary. Before the 2026-09-17 pass this endpoint's failures went through
// `describeBookingRejection`, which knows three controls and calls everything
// else "the selected time is not valid — go back to availability" — a screen
// that feeds none of a series' fields.

function problem(status: number, body: object): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText: 'Error',
    error: { title: 'Rejected', status, reasonCode: 'ValidationFailed', correlationId: 'c1', ...body },
  });
}

function validationFailure(errors: Record<string, string[]>): HttpErrorResponse {
  return problem(400, { errors });
}

describe('describeRecurrenceRejection', () => {
  // The fields `CreateRecurrenceSeriesCommandRequestValidator` actually
  // reports against, each landing on the control that holds it.
  describe('validation failures', () => {
    it.each([
      ['StartDate', 'startDate'],
      ['IntervalValue', 'interval'],
      ['OccurrenceCount', 'occurrenceCount'],
      ['EndDate', 'endDate'],
      ['LocalStartTime', 'times'],
      ['LocalEndTime', 'times'],
      ['Title', 'title'],
      ['Quantity', 'quantity'],
    ] as const)('puts a %s failure against the %s control', (backendField, formField) => {
      const rejection = describeRecurrenceRejection(validationFailure({ [backendField]: ['Server says no.'] }));

      expect(rejection.fieldMessages[formField]).toBe('Server says no.');
      expect(rejection.formMessage).toBeNull();
      expect(rejection.recheckAvailability).toBe(false);
    });

    // The regression this pass exists to prevent, stated as a contrast: the
    // one-off mapper answers the identical payload by sending the member to a
    // screen that cannot change an occurrence count.
    it('no longer sends an occurrence-count failure back to availability', () => {
      const payload = validationFailure({ OccurrenceCount: ['Must be greater than zero.'] });

      const throughOneOff = describeBookingRejection(payload);
      expect(throughOneOff.formMessage).toContain('Go back to availability');
      expect(throughOneOff.recheckAvailability).toBe(true);

      const throughRecurrence = describeRecurrenceRejection(payload);
      expect(throughRecurrence.formMessage).toBeNull();
      expect(throughRecurrence.recheckAvailability).toBe(false);
      expect(throughRecurrence.fieldMessages.occurrenceCount).toBe('Must be greater than zero.');
    });

    // ResourceId and IdempotencyKey are the only fields this form renders no
    // control for — so they get a top-of-form message, and still not one
    // pointing at the availability screen.
    it('reports a failure on a field the form has no control for at the top', () => {
      const rejection = describeRecurrenceRejection(validationFailure({ ResourceId: ['Required.'] }));

      expect(rejection.formMessage).toContain('could not be created');
      expect(rejection.formMessage).not.toContain('availability');
      expect(rejection.recheckAvailability).toBe(false);
    });

    it('keeps every mapped field when several fail at once', () => {
      const rejection = describeRecurrenceRejection(
        validationFailure({ StartDate: ['Bad date.'], OccurrenceCount: ['Too many.'] }),
      );

      expect(rejection.fieldMessages).toEqual({ startDate: 'Bad date.', occurrenceCount: 'Too many.' });
    });
  });

  describe('rule refusals', () => {
    // Every occurrence shares the series' nominal length, so the handler asks
    // Resource.AllowsBookingDuration once for the series as a whole.
    it('puts a duration refusal against the controls that decide it', () => {
      const rejection = describeRecurrenceRejection(
        problem(422, { reasonCode: 'BookingDurationOutOfRange' }),
      );

      expect(rejection.fieldMessages.duration).toContain('outside what the resource allows');
      expect(rejection.formMessage).toBeNull();
    });

    it('states an archived resource in series terms, with no action', () => {
      const rejection = describeRecurrenceRejection(problem(422, { reasonCode: 'ResourceArchived' }));

      expect(rejection.formMessage).toContain('no new series');
      expect(rejection.recheckAvailability).toBe(false);
    });

    it('switches to the not-found state for a missing resource', () => {
      const rejection = describeRecurrenceRejection(problem(404, { reasonCode: 'ResourceNotFound' }));

      expect(rejection.resourceNotFound).toBe(true);
    });

    // A per-occurrence code can never be the series' own reason code — those
    // ride in the FR-5.4 breakdown, which `parseSeriesRefusal` takes first —
    // so if one somehow arrives here it is an unrecognized refusal, not an
    // invitation to re-pick a slot the series never had.
    it.each(['SlotUnavailable', 'CapacityExceeded', 'OutsideAvailability', 'BlackoutPeriod'])(
      'offers no availability re-check for a stray %s',
      (reasonCode) => {
        const rejection = describeRecurrenceRejection(problem(422, { reasonCode }));

        expect(rejection.recheckAvailability).toBe(false);
        expect(rejection.formMessage).toBe('This series could not be created.');
      },
    );
  });

  // The recurring endpoint carries an idempotency key, but nothing about that
  // changes what is *known* here — only what the component may offer.
  describe('an outcome nobody could observe', () => {
    it.each([0, 500, 503])('flags status %s as possibly created, in series terms', (status) => {
      const rejection = describeRecurrenceRejection(
        new HttpErrorResponse({ status, statusText: 'Error', error: new ProgressEvent('error') }),
      );

      expect(rejection.outcomeUnknown).toBe(true);
      expect(rejection.formMessage).toContain('series may have been created');
      expect(rejection.formMessage).toContain('Check your calendar');
    });
  });
});
