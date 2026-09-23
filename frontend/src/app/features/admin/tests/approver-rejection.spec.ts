import { HttpErrorResponse } from '@angular/common/http';
import { describeApproverRejection } from '../rejection/approver-rejection';

// Admin console phase 7, coverage sweep. **The seventh dialect shipped in phase
// 5 with no spec of its own** — missed because the sweep that preceded it
// compared names by eye, and `approver-rejection.ts` has a name so close to its
// six covered siblings that nothing stood out. WP-7 Phase 7 learned exactly this
// once already (`blackout-periods.service.ts`, missed for sitting one folder
// over); enumerating the files rather than scanning the names is what found both.

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

describe('describeApproverRejection', () => {
  // **The refusal this screen is built to make unreachable, and cannot quite.**
  // The picker only offers people `GET /users` returned, so every id it sends
  // was eligible when the page loaded; reaching this means somebody was
  // deactivated, lost a role, or left the tenant in between.
  //
  // What the copy must *not* do is name the person or the cause. Decision `0018`
  // collapses all three causes into one code deliberately, because naming one
  // would confirm a cross-tenant id exists (AC-4) — so these assertions are
  // about a security property, not about tone.
  it('explains an ineligible approver without naming the person or the reason', () => {
    const rejection = describeApproverRejection(problem(422, 'ApproverNotEligible'));

    expect(rejection.formMessage).toContain('can no longer approve');
    expect(rejection.formMessage).toContain('Reload');
    // No id, and no single cause asserted as the one that happened.
    expect(rejection.formMessage).toContain('may');
    expect(rejection.fieldMessages).toEqual({});
  });

  // Decision `0028` deleted both the rule and the code, so an empty approver
  // list on a gated resource is now accepted. A dialect still carrying copy for
  // it would be describing a refusal the API can no longer produce — CLAUDE.md
  // §6's rule that the catalogue describes what the API actually returns.
  it('has no copy for ApproversRequired, which decision 0028 deleted', () => {
    expect(describeApproverRejection(problem(422, 'ApproversRequired')).formMessage).toBe(
      'These approvers could not be saved.',
    );
  });

  it('flags a missing resource separately, so the screen can switch state', () => {
    const rejection = describeApproverRejection(problem(404, 'ResourceNotFound'));

    expect(rejection.resourceNotFound).toBe(true);
    expect(rejection.formMessage).toBeNull();
  });

  // FR-3.5. A dead end rather than something to retry, and the copy says why
  // there is no way back.
  it('says an archived resource can no longer have its approvers changed', () => {
    const rejection = describeApproverRejection(problem(422, 'ResourceArchived'));

    expect(rejection.formMessage).toContain('archived');
    expect(rejection.formMessage).toContain('cannot be undone');
  });

  // `Resources` has carried a RowVersion since the 2026-09-15 hardening pass, so
  // this is reachable here. The copy must say *reload*, not *save again* —
  // saving again is precisely how the other admin's list gets overwritten.
  it('tells a losing admin to reload rather than re-send', () => {
    const rejection = describeApproverRejection(problem(409, 'ConcurrencyConflict'));

    expect(rejection.formMessage).toContain('Reload before');
    expect(rejection.formMessage).toContain('replace their list with yours');
  });

  // There is no control to point a message at: the form is tick boxes for
  // people, and a validation failure names `ApproverUserIds`, which is the array
  // rather than any one of them.
  it('puts a validation failure on the form, never on a control', () => {
    const rejection = describeApproverRejection(
      problem(400, 'ValidationFailed', { ApproverUserIds: ['Duplicate ids are not allowed.'] }),
    );

    expect(rejection.fieldMessages).toEqual({});
    expect(rejection.formMessage).toContain("wasn't valid");
  });

  // **Replace-the-set is idempotent by construction**, so this is one of the two
  // dialects that may honestly offer a retry — sending the same selection twice
  // produces the same list. The blackout dialect, one screen over, must not.
  it('offers a safe retry on an unknown outcome, because the write is idempotent', () => {
    const rejection = describeApproverRejection(
      new HttpErrorResponse({ status: 0, statusText: 'Unknown Error' }),
    );

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('save again');
    expect(rejection.formMessage).toContain('harmless');
  });

  it('treats a 5xx as the same unknown outcome', () => {
    expect(describeApproverRejection(problem(503)).outcomeUnknown).toBe(true);
  });

  it('falls back to a plain refusal for a code it does not know', () => {
    expect(describeApproverRejection(problem(422, 'SomeFutureRule')).formMessage).toBe(
      'These approvers could not be saved.',
    );
  });

  // Nothing on an admin screen should suggest re-checking availability: there is
  // no slot involved in choosing who approves a room.
  it('never suggests re-checking availability', () => {
    expect(describeApproverRejection(problem(422, 'ApproverNotEligible')).recheckAvailability).toBe(
      false,
    );
  });
});
