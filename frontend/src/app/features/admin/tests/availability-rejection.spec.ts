import { HttpErrorResponse } from '@angular/common/http';
import { describeAvailabilityRejection } from '../rejection/availability-rejection';

// Admin console phase 4. The sixth dialect.
//
// Tested at this level and not only through the editor, for the reason WP-7
// Phase 7 recorded when it gave `approval-rejection.ts` its own spec: proving a
// mapper through a component proves the two are wired together, not that the
// mapping is right — and here the mapping *is* the product, because every one
// of these is a sentence an administrator has to act on.
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

describe('describeAvailabilityRejection', () => {
  // The editor refuses overlaps itself, so reaching this means the two rules
  // drifted or something else sent the request. Worth wording precisely anyway:
  // a 409 an admin cannot explain is worse than one that says what was compared.
  it('explains an overlap, and says adjacency is allowed', () => {
    const rejection = describeAvailabilityRejection(problem(409, 'OverlappingAvailabilityWindow'));

    expect(rejection.formMessage).toContain('overlap');
    expect(rejection.formMessage).toContain('ends exactly when the next begins is fine');
  });

  it('says an archived resource cannot have its hours changed, and that it is final', () => {
    const rejection = describeAvailabilityRejection(problem(422, 'ResourceArchived'));

    expect(rejection.formMessage).toContain('archived');
    expect(rejection.formMessage).toContain('cannot be undone');
  });

  it('tells the admin to reload on a concurrency conflict rather than overwrite', () => {
    const rejection = describeAvailabilityRejection(problem(409, 'ConcurrencyConflict'));

    expect(rejection.formMessage).toContain('Reload');
    expect(rejection.formMessage).toContain('replace their schedule');
  });

  it('flags a missing resource rather than producing a form message', () => {
    expect(describeAvailabilityRejection(problem(404, 'ResourceNotFound')).resourceNotFound).toBe(true);
  });

  // **FluentValidation names a window by its index** (`Windows[2].ClosesAt`),
  // which is a position in an array this screen sorted before sending — so it
  // cannot be resolved back to the row the admin is looking at. Deliberately
  // unmapped: saying a window was refused beats pointing at the wrong one.
  it('does not try to point an indexed window failure at a row', () => {
    const rejection = describeAvailabilityRejection(
      problem(400, 'ValidationFailed', { 'Windows[2].ClosesAt': ['ClosesAt must be after OpensAt.'] }),
    );

    expect(rejection.formMessage).toContain('closes after it opens');
    expect(rejection.fieldMessages).toEqual({});
  });

  // **The one admin write that is genuinely safe to repeat.** Replace-the-set is
  // idempotent by construction — the same schedule sent twice is the same
  // schedule — so unlike the booking and decision dialects this one can offer
  // the retry rather than hedging.
  it('says an unknown outcome is safe to retry, which no other dialect does', () => {
    const rejection = describeAvailabilityRejection(new HttpErrorResponse({ status: 0, statusText: 'Unknown' }));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('save again');
    expect(rejection.formMessage).toContain('harmless');
  });

  it('treats a 5xx the same way', () => {
    expect(describeAvailabilityRejection(problem(500)).outcomeUnknown).toBe(true);
  });

  it('falls back to a plain refusal for a code it does not know', () => {
    expect(describeAvailabilityRejection(problem(422, 'SomeFutureRule')).formMessage).toBe(
      'This schedule could not be saved.',
    );
  });

  it('never suggests re-checking availability — there is no slot being picked here', () => {
    expect(describeAvailabilityRejection(problem(409, 'OverlappingAvailabilityWindow')).recheckAvailability).toBe(
      false,
    );
  });
});
