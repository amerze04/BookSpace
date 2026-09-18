import { HttpErrorResponse } from '@angular/common/http';
import { describeBookingRejection } from '../rejection/booking-rejection';

// A real ProblemDetails, the shape GlobalExceptionHandler actually writes
// (confirmed against the running API during step 1).
function problem(status: number, reasonCode: string, errors?: Record<string, string[]>): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText: 'Error',
    error: {
      title: 'The request was rejected by a rule.',
      status,
      reasonCode,
      correlationId: 'c1',
      ...(errors ? { errors } : {}),
    },
  });
}

describe('describeBookingRejection', () => {
  // The 409s: the slot moved, so looking again is worth doing.
  describe('a slot that was taken', () => {
    it('sends SlotUnavailable to the top of the form with a way to re-check', () => {
      const rejection = describeBookingRejection(problem(409, 'SlotUnavailable'));

      expect(rejection.formMessage).toContain('booked this time while you were filling in the form');
      expect(rejection.recheckAvailability).toBe(true);
      expect(rejection.fieldMessages).toEqual({});
      expect(rejection.outcomeUnknown).toBe(false);
    });

    // CLAUDE.md §6: the two 409s split by what is *left*, so only this one
    // mentions quantity — and it can only ever appear on a pooled resource.
    it('words CapacityExceeded as fewer units than were asked for', () => {
      const rejection = describeBookingRejection(problem(409, 'CapacityExceeded'));

      expect(rejection.formMessage).toContain('Fewer units are free');
      expect(rejection.recheckAvailability).toBe(true);
    });

    it('treats ConcurrencyConflict as re-checkable too', () => {
      const rejection = describeBookingRejection(problem(409, 'ConcurrencyConflict'));

      expect(rejection.formMessage).toContain('changed while your booking was being created');
      expect(rejection.recheckAvailability).toBe(true);
    });
  });

  // The 422s: a rule refused it, so offering "check availability" would be
  // suggesting that trying again might work when it cannot.
  describe('a rule refusal', () => {
    it.each([
      ['ResourceArchived', 'archived'],
      ['BlackoutPeriod', 'blackout period'],
      ['OutsideAvailability', 'outside the resource'],
      ['BookingInThePast', 'already passed'],
    ])('states %s at the top of the form with no re-check action', (reasonCode, expected) => {
      const rejection = describeBookingRejection(problem(422, reasonCode));

      expect(rejection.formMessage).toContain(expected);
      expect(rejection.recheckAvailability).toBe(false);
      expect(rejection.fieldMessages).toEqual({});
    });
  });

  describe('a control to blame', () => {
    it('puts BookingDurationOutOfRange against the duration, not the top of the form', () => {
      const rejection = describeBookingRejection(problem(422, 'BookingDurationOutOfRange'));

      expect(rejection.fieldMessages.duration).toContain('length is outside what the resource allows');
      expect(rejection.formMessage).toBeNull();
    });

    // FluentValidation reports PascalCase C# property names.
    it('maps ValidationFailed field errors onto the form\'s own controls', () => {
      const rejection = describeBookingRejection(
        problem(400, 'ValidationFailed', {
          Title: ['The length of Title must be 200 characters or fewer.'],
          Quantity: ['Quantity must be greater than zero.'],
        }),
      );

      expect(rejection.fieldMessages).toEqual({
        title: 'The length of Title must be 200 characters or fewer.',
        quantity: 'Quantity must be greater than zero.',
      });
      expect(rejection.formMessage).toBeNull();
      expect(rejection.recheckAvailability).toBe(false);
    });

    // The instants have no control on this form — they come from the URL — so
    // a failure on one is a top-of-form message pointing back at availability,
    // not a message aimed at a field the member cannot see.
    it('turns a failure on a field this form has no control for into a re-pick', () => {
      const rejection = describeBookingRejection(
        problem(400, 'ValidationFailed', {
          StartsAtUtc: ['StartsAtUtc must not carry fractional seconds.'],
        }),
      );

      expect(rejection.formMessage).toContain('pick a slot again');
      expect(rejection.recheckAvailability).toBe(true);
      expect(rejection.fieldMessages).toEqual({});
    });

    it('reports both halves when one field maps and another does not', () => {
      const rejection = describeBookingRejection(
        problem(400, 'ValidationFailed', {
          Title: ['Too long.'],
          EndsAtUtc: ['EndsAtUtc must be after StartsAtUtc.'],
        }),
      );

      expect(rejection.fieldMessages.title).toBe('Too long.');
      expect(rejection.formMessage).toContain('pick a slot again');
    });
  });

  describe('the resource itself', () => {
    it('hands ResourceNotFound to the screen\'s own not-found state', () => {
      const rejection = describeBookingRejection(problem(404, 'ResourceNotFound'));

      expect(rejection.resourceNotFound).toBe(true);
      expect(rejection.formMessage).toBeNull();
    });
  });

  describe('an outcome nobody can confirm', () => {
    // POST /bookings has no idempotency key (wp7-plan.md §7), so the one thing
    // this must never do is invite a retry.
    it('says a booking may exist when the request never reached a server', () => {
      const rejection = describeBookingRejection(
        new HttpErrorResponse({ status: 0, statusText: 'Unknown Error', error: new ProgressEvent('error') }),
      );

      expect(rejection.outcomeUnknown).toBe(true);
      expect(rejection.formMessage).toContain('may have been created');
      expect(rejection.recheckAvailability).toBe(false);
    });

    // A 5xx *did* reach the server, so dbo.CreateBooking may well have
    // committed before whatever failed — the same unknown outcome.
    it('says the same for a 5xx', () => {
      const rejection = describeBookingRejection(
        new HttpErrorResponse({ status: 500, statusText: 'Server Error', error: 'boom' }),
      );

      expect(rejection.outcomeUnknown).toBe(true);
      expect(rejection.formMessage).toContain('may have been created');
    });

    // The ordering that makes the status-0 case work at all: its `error` is a
    // ProgressEvent, so a reason-code branch would find nothing on it.
    it('checks status 0 before looking for a reason code', () => {
      const rejection = describeBookingRejection(
        new HttpErrorResponse({
          status: 0,
          statusText: 'Unknown Error',
          // Even if something ProblemDetails-shaped rode along, status 0 wins:
          // no server answered, so no code it carried could be trusted.
          error: { title: 'x', status: 409, reasonCode: 'SlotUnavailable', correlationId: 'c1' },
        }),
      );

      expect(rejection.outcomeUnknown).toBe(true);
      expect(rejection.recheckAvailability).toBe(false);
    });
  });

  describe('anything else', () => {
    it('states an unrecognized reason code plainly rather than inventing one', () => {
      const rejection = describeBookingRejection(problem(422, 'SomeFutureCode'));

      expect(rejection.formMessage).toBe('This booking could not be created.');
      expect(rejection.recheckAvailability).toBe(false);
      expect(rejection.outcomeUnknown).toBe(false);
    });

    it('handles a non-HTTP error', () => {
      expect(describeBookingRejection(new TypeError('boom')).formMessage).toBe(
        'This booking could not be created.',
      );
    });

    it('handles a 4xx whose body is not a ProblemDetails', () => {
      const rejection = describeBookingRejection(
        new HttpErrorResponse({ status: 403, statusText: 'Forbidden', error: 'nope' }),
      );

      expect(rejection.formMessage).toBe('This booking could not be created.');
      expect(rejection.outcomeUnknown).toBe(false);
    });
  });
});
