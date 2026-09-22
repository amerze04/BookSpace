import { HttpErrorResponse } from '@angular/common/http';
import { describeDecisionRejection } from '../rejection/approval-rejection';

// WP-7 Phase 7 step 2. The fourth dialect was the only one covered solely
// through a component (`decision-panel.component.spec.ts`); its three siblings
// each have a spec at this level, and so should it. Testing a mapper through a
// component proves the two are wired together, not that the mapping is right —
// and the AC-5 arm in particular is a *message*, which is the whole reason it
// exists.
//
// `isProblemDetails` requires `title` as well as `reasonCode`, so a body missing
// it falls through to the generic message. That is exactly the mistake the
// decision panel's first fixtures made — five tests passing while asserting the
// wrong thing — so this helper builds the real shape, matching how
// `cancel-rejection.spec.ts` already does it.
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

describe('describeDecisionRejection', () => {
  // **AC-5.** The slot went while the request sat in the queue —
  // `dbo.ApproveBooking` re-runs the capacity check under its lock. The two
  // codes are one event to an approver, however the procedure classified what
  // was left, so both carry the same words.
  it('explains a since-taken slot as its own event, for both capacity codes', () => {
    for (const code of ['SlotUnavailable', 'CapacityExceeded']) {
      const rejection = describeDecisionRejection(problem(409, code));

      expect(rejection.formMessage).toContain('taken while the request was waiting');
      expect(rejection.formMessage).toContain('Nothing has been decided');
      expect(rejection.outcomeUnknown).toBe(false);
    }
  });

  // The message has to leave the approver somewhere to go. Approving is off the
  // table; rejecting is not, and the copy says so — that is the difference
  // between a dead end and an outcome.
  it('tells the approver that rejecting is still possible when approval is refused', () => {
    for (const code of ['SlotUnavailable', 'BlackoutPeriod', 'ResourceArchived']) {
      expect(describeDecisionRejection(problem(409, code)).formMessage).toContain('still');
    }
  });

  // The concurrent-decision case, verified live in Phase 6 step 6: two callers,
  // one 200 and one 422. The client cannot tell "someone else decided" from
  // "already cancelled or expired" and does not need to — either way this
  // decision did not land, and looking beats trying again.
  it('says a request has already been decided, without guessing by whom', () => {
    const rejection = describeDecisionRejection(problem(422, 'BookingNotPending'));

    expect(rejection.formMessage).toContain('already been decided');
    expect(rejection.formMessage).toContain('Reload');
  });

  it('explains a 404 as the request no longer being reachable', () => {
    const rejection = describeDecisionRejection(problem(404, 'BookingNotFound'));

    expect(rejection.formMessage).toContain('no longer available to you');
  });

  it('explains a blackout that appeared since the request was made', () => {
    const rejection = describeDecisionRejection(problem(422, 'BlackoutPeriod'));

    expect(rejection.formMessage).toContain('blocked out over this time');
  });

  it('explains an archived resource', () => {
    const rejection = describeDecisionRejection(problem(422, 'ResourceArchived'));

    expect(rejection.formMessage).toContain('archived');
  });

  // FluentValidation reports under the C# property name. `Note` maps onto the
  // `reason` control — the same field name the cancel dialect uses, reused
  // rather than widening `BookingFieldName`, because both are "the optional
  // words an actor attaches to a decision" and the two are never on screen
  // together.
  it('puts an over-long note on the note control, not above the buttons', () => {
    const rejection = describeDecisionRejection(
      problem(400, 'ValidationFailed', { Note: ['The length of Note must be 500 characters or fewer.'] }),
    );

    expect(rejection.fieldMessages.reason).toContain('500 characters');
    expect(rejection.formMessage).toBeNull();
  });

  // **No retry is ever offered on this path**, which is why both unknown-outcome
  // arms send the approver to look rather than to act. A decision is not
  // idempotent — a repeat answers 422 — and on approve a repeat could lose the
  // capacity race a second time.
  it('reports an unreachable server as an unknown outcome, never as a retry', () => {
    const rejection = describeDecisionRejection(new HttpErrorResponse({ status: 0, statusText: 'Unknown' }));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('may or may not have been recorded');
    expect(rejection.formMessage).not.toMatch(/try again/i);
  });

  it('treats a 5xx the same way — the decision may well have landed', () => {
    const rejection = describeDecisionRejection(problem(500));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('may or may not have been recorded');
  });

  // A refusal this client has no copy for is still a refusal. Saying so plainly
  // beats inventing an explanation for a rule it does not know about.
  it('falls back to a plain refusal for an unrecognized code', () => {
    const rejection = describeDecisionRejection(problem(422, 'SomeFutureRule'));

    expect(rejection.formMessage).toBe('This request could not be decided.');
    expect(rejection.outcomeUnknown).toBe(false);
  });

  it('does not throw on something that is not an HTTP error at all', () => {
    const rejection = describeDecisionRejection(new Error('boom'));

    expect(rejection.formMessage).toBe('This request could not be decided.');
  });

  // Nothing on a decision path should ever suggest going back to availability —
  // an approver is not picking a slot, and there is no slot of theirs to pick.
  // The flag exists for the booking form's dialect and must stay false here.
  it('never suggests re-checking availability', () => {
    for (const code of ['SlotUnavailable', 'CapacityExceeded', 'BookingNotPending', 'BlackoutPeriod']) {
      expect(describeDecisionRejection(problem(409, code)).recheckAvailability).toBe(false);
    }
  });
});
