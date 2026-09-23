import { HttpErrorResponse } from '@angular/common/http';
import { describeBlackoutRejection } from '../rejection/blackout-rejection';

// Admin console phase 6. The eighth and last dialect, and the only one covering
// three endpoints — create, edit and delete share an audience and a vocabulary.

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

describe('describeBlackoutRejection', () => {
  // **Decision `0019`'s rule, and the one most likely to surprise.** It is about
  // the *end* being in the past, not the start — a blackout that began this
  // morning and runs through tomorrow is legal, and is exactly what an admin
  // needs when a room floods. The copy has to say that, or it reads as "you
  // cannot black out something that has started".
  it('explains an elapsed blackout, and says a started one is fine', () => {
    const rejection = describeBlackoutRejection(problem(422, 'BlackoutPeriodElapsed'));

    expect(rejection.fieldMessages.endsAt).toContain('already finished');
    expect(rejection.fieldMessages.endsAt).toContain('already started is fine');
  });

  // A 404 on delete is deliberate rather than a 204, because the endpoint cannot
  // tell "already deleted" from "another tenant's" (AC-4). Either way the row is
  // not there to act on.
  it('says a missing blackout may already have been deleted', () => {
    const rejection = describeBlackoutRejection(problem(404, 'BlackoutPeriodNotFound'));

    expect(rejection.formMessage).toContain('no longer exists');
    expect(rejection.formMessage).toContain('Reload');
  });

  it('flags a missing resource separately, so the screen can switch state', () => {
    expect(describeBlackoutRejection(problem(404, 'ResourceNotFound')).resourceNotFound).toBe(true);
  });

  it('says an archived resource can no longer have its blackouts changed', () => {
    expect(describeBlackoutRejection(problem(422, 'ResourceArchived')).formMessage).toContain(
      'archived',
    );
  });

  // Reachable here in a way it is not on the other admin screens: the cascade
  // writes to `Bookings`, which carry their own RowVersion.
  it('explains a concurrency conflict as a booking having moved underneath', () => {
    const rejection = describeBlackoutRejection(problem(409, 'ConcurrencyConflict'));

    expect(rejection.formMessage).toContain('Nothing was');
    expect(rejection.formMessage).toContain('reload');
  });

  it('routes a field validation failure to its own control', () => {
    const rejection = describeBlackoutRejection(
      problem(400, 'ValidationFailed', { EndsAtUtc: ['EndsAtUtc must be after StartsAtUtc.'] }),
    );

    expect(rejection.fieldMessages.endsAt).toContain('must be after');
    expect(rejection.formMessage).toBeNull();
  });

  // **No retry, and this is the path where that matters most.** A blackout write
  // is not idempotent in any useful sense — repeating a create makes a *second*
  // blackout, decision `0019` allows overlaps so nothing refuses it, and the
  // first attempt may already have cancelled bookings the forwards-only cascade
  // will never restore.
  it('refuses to suggest a retry on an unknown outcome, and says why', () => {
    const rejection = describeBlackoutRejection(new HttpErrorResponse({ status: 0, statusText: 'Unknown' }));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('second blackout');
    expect(rejection.formMessage).toContain('already have cancelled bookings');
  });

  it('treats a 5xx the same way', () => {
    expect(describeBlackoutRejection(problem(500)).outcomeUnknown).toBe(true);
  });

  it('falls back to a plain refusal for a code it does not know', () => {
    expect(describeBlackoutRejection(problem(422, 'SomeFutureRule')).formMessage).toBe(
      'This blackout could not be saved.',
    );
  });

  it('never suggests re-checking availability', () => {
    expect(describeBlackoutRejection(problem(422, 'BlackoutPeriodElapsed')).recheckAvailability).toBe(
      false,
    );
  });
});
