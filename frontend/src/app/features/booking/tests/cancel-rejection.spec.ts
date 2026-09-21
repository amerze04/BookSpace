import { HttpErrorResponse } from '@angular/common/http';
import {
  describeCancelRejection,
  describeSeriesCancelRejection,
} from '../rejection/cancel-rejection';

// `isProblemDetails` requires `title` as well as `reasonCode`, so a body
// missing it falls through to the generic message — which is what a real
// ProblemDetails always carries anyway.
function problem(status: number, reasonCode?: string, errors?: Record<string, string[]>) {
  return new HttpErrorResponse({
    status,
    statusText: 'Error',
    error: {
      status,
      title: 'The request was refused.',
      ...(reasonCode ? { reasonCode } : {}),
      ...(errors ? { errors } : {}),
    },
  });
}

describe('describeCancelRejection', () => {
  // The two halves `Booking.CanBeCancelled` tests — already terminal, or
  // already ended. The client cannot tell which applies, so the message names
  // both and sends the member to look rather than to try again: its own copy of
  // the rule has just been proven stale.
  it('explains a refusal to cancel without claiming which half failed', () => {
    const rejection = describeCancelRejection(problem(422, 'BookingNotCancellable'));

    expect(rejection.formMessage).toContain('already ended or already been cancelled');
    expect(rejection.outcomeUnknown).toBe(false);
    expect(rejection.recheckAvailability).toBe(false);
  });

  it('explains a 404 as the booking no longer being available', () => {
    const rejection = describeCancelRejection(problem(404, 'BookingNotFound'));

    expect(rejection.formMessage).toContain('no longer available to you');
  });

  // RowVersion picks one of two simultaneous cancels. The loser's booking *is*
  // cancelled, just not by them — so the advice is to look, not to retry, which
  // would only answer BookingNotCancellable.
  it('explains a concurrency conflict as someone else having changed it', () => {
    const rejection = describeCancelRejection(problem(409, 'ConcurrencyConflict'));

    expect(rejection.formMessage).toContain('changed this booking at the same moment');
  });

  it('puts an over-long reason on the reason control', () => {
    const rejection = describeCancelRejection(
      problem(400, 'ValidationFailed', { Reason: ['Too long.'] }),
    );

    expect(rejection.fieldMessages.reason).toBe('Too long.');
    expect(rejection.formMessage).toBeNull();
  });

  // **The reason this dialect exists rather than reusing the one-off map**: the
  // create vocabulary would have sent a cancelling member back to the
  // availability screen, which has nothing to do with cancelling.
  it('never suggests re-checking availability', () => {
    for (const error of [
      problem(422, 'BookingNotCancellable'),
      problem(404, 'BookingNotFound'),
      problem(409, 'ConcurrencyConflict'),
      problem(400, 'ValidationFailed', { BookingId: ['Required.'] }),
      problem(500),
    ]) {
      expect(describeCancelRejection(error).recheckAvailability).toBe(false);
    }
  });

  // A repeat would overwrite who called the meeting off, so both unobservable
  // outcomes say the same thing: reload rather than try again.
  it.each([0, 500, 503])('reports status %i as an unknown outcome, never a retry', (status) => {
    const rejection = describeCancelRejection(problem(status));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('may or may not have gone through');
    expect(rejection.formMessage).toContain('rather than trying again');
  });

  it('falls back to a plain refusal for a code it has no copy for', () => {
    const rejection = describeCancelRejection(problem(422, 'SomethingNewEntirely'));

    expect(rejection.formMessage).toBe('This booking could not be cancelled.');
  });
});

describe('describeSeriesCancelRejection', () => {
  // **`RecurrenceRule.CanBeCancelled()` is `Status == Active` and nothing
  // else** — no time component, unlike the per-booking rule. So this code means
  // exactly one thing and the copy says it, where the booking message has to
  // hedge between "already ended" and "already cancelled".
  it('states plainly that the series is already cancelled', () => {
    const rejection = describeSeriesCancelRejection(problem(422, 'RecurrenceRuleNotCancellable'));

    expect(rejection.formMessage).toContain('already been cancelled');
    expect(rejection.formMessage).not.toContain('already ended');
  });

  it('explains a 404 in series terms', () => {
    const rejection = describeSeriesCancelRejection(problem(404, 'RecurrenceRuleNotFound'));

    expect(rejection.formMessage).toContain('This series is no longer available');
  });

  // The reason the two dialects are separate rather than one merged map: they
  // share this code and it has to read differently.
  it('says "series" where the booking dialect says "booking", on a shared code', () => {
    const series = describeSeriesCancelRejection(problem(409, 'ConcurrencyConflict'));
    const booking = describeCancelRejection(problem(409, 'ConcurrencyConflict'));

    expect(series.formMessage).toContain('this series');
    expect(booking.formMessage).toContain('this booking');
  });

  it('puts an over-long reason on the same reason control', () => {
    const rejection = describeSeriesCancelRejection(
      problem(400, 'ValidationFailed', { Reason: ['Too long.'] }),
    );

    expect(rejection.fieldMessages.reason).toBe('Too long.');
  });

  it.each([0, 500])('reports status %i as an unknown outcome, never a retry', (status) => {
    const rejection = describeSeriesCancelRejection(problem(status));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('rather than trying again');
  });
});
