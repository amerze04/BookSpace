import { HttpErrorResponse } from '@angular/common/http';
import { describeUserRejection } from '../rejection/user-rejection';

// The ninth dialect over `core/http/rejection.ts`. Its own spec because admin
// console phase 7's coverage sweep found `approver-rejection.ts` was the one of
// eight without one — a dialect is copy plus a mapping table, and both are the
// kind of thing that is only ever wrong in a way nothing else notices.
//
// Two of these assertions are about a security property rather than tone. See
// the `EmailAlreadyInUse` block.

function problem(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, statusText: 'Error', error: body });
}

describe('describeUserRejection', () => {
  it('points a taken address at the email control', () => {
    const rejection = describeUserRejection(
      problem(409, { status: 409, title: 'Conflict', reasonCode: 'EmailAlreadyInUse' }),
    );

    expect(rejection.fieldMessages.email).toContain('already has an account');
    expect(rejection.formMessage).toBeNull();
  });

  // **Decisions `0010` and `0030`.** Email is unique platform-wide, so a
  // collision may be with an account in an organization this administrator
  // cannot see — and the server answers identically either way, precisely so
  // `POST /users` cannot be used to discover which. The copy has to hold that
  // line; naming a tenant would give away what the status code deliberately
  // does not.
  it('does not say where a taken address lives', () => {
    const rejection = describeUserRejection(
      problem(409, { status: 409, title: 'Conflict', reasonCode: 'EmailAlreadyInUse' }),
    );

    const message = (rejection.fieldMessages.email ?? '').toLowerCase();
    expect(message).not.toContain('another organization');
    expect(message).not.toContain('another tenant');
    expect(message).not.toContain('your organization');
    expect(message).not.toContain('this tenant');
  });

  it('maps a validation failure onto its control', () => {
    const rejection = describeUserRejection(
      problem(400, {
        status: 400,
        title: 'Validation',
        reasonCode: 'ValidationFailed',
        errors: { Email: ['Email is required.'], FullName: ['FullName is required.'] },
      }),
    );

    expect(rejection.fieldMessages.email).toBe('Email is required.');
    expect(rejection.fieldMessages.fullName).toBe('FullName is required.');
    expect(rejection.formMessage).toBeNull();
  });

  it('falls back to a form message when a validation failure names a field this form has no control for', () => {
    const rejection = describeUserRejection(
      problem(400, {
        status: 400,
        title: 'Validation',
        reasonCode: 'ValidationFailed',
        errors: { OrgId: ['Something about a field this form never sends.'] },
      }),
    );

    expect(rejection.formMessage).toContain('not valid');
    expect(rejection.fieldMessages).toEqual({});
  });

  it('says plainly that it does not recognize a refusal', () => {
    const rejection = describeUserRejection(
      problem(422, { status: 422, title: 'Rule', reasonCode: 'SomeRuleThisClientHasNeverHeardOf' }),
    );

    expect(rejection.formMessage).toBe('This account could not be created.');
  });

  // **The strictest unknown-outcome copy in the console.** `POST /users` has no
  // idempotency key, so repeating it either creates a second account or comes
  // back 409 about the one it just made — and the first attempt may already
  // have sent the invitation, which nothing can withdraw or re-issue.
  it.each([
    ['a request that reached no server', 0],
    ['a server that could not report what happened', 500],
    ['a gateway failure', 503],
  ])('reports an unknown outcome for %s, and offers no retry', (_label, status) => {
    const rejection = describeUserRejection(problem(status, null));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('Check the user list');
    expect(rejection.formMessage?.toLowerCase()).not.toContain('try again now');
  });

  it('treats a 4xx whose body is not a ProblemDetails as a plain refusal', () => {
    const rejection = describeUserRejection(problem(400, 'not json'));

    expect(rejection.formMessage).toBe('This account could not be created.');
    expect(rejection.outcomeUnknown).toBe(false);
  });

  it('handles something that is not an HttpErrorResponse at all', () => {
    const rejection = describeUserRejection(new Error('boom'));

    expect(rejection.formMessage).toBe('This account could not be created.');
  });

  // `ResourceNotFound` belongs to the shared machinery and cannot arrive on
  // this endpoint. Asserted so that a later reader does not wire a not-found
  // state on this screen for a case that never happens.
  it('never reports a missing resource, because this endpoint has none', () => {
    const rejection = describeUserRejection(
      problem(409, { status: 409, title: 'Conflict', reasonCode: 'EmailAlreadyInUse' }),
    );

    expect(rejection.resourceNotFound).toBe(false);
    expect(rejection.recheckAvailability).toBe(false);
  });
});
