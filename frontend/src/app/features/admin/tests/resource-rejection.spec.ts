import { HttpErrorResponse } from '@angular/common/http';
import { describeResourceRejection } from '../rejection/resource-rejection';

// Admin console phase 2. The fifth dialect, and the first over the machinery
// `core/http/rejection.ts` now owns — so this spec doubles as the proof that
// the phase-2 extraction actually works for a field vocabulary that is not the
// booking form's.
//
// `isProblemDetails` requires `title` as well as `reasonCode`, so a body
// missing it falls through to the generic message. That is exactly the mistake
// the decision panel's first fixtures made in WP-7 — five tests passing while
// asserting the wrong thing — so this helper builds the real shape.
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

describe('describeResourceRejection', () => {
  // **FR-3.3, and the one refusal whose fix is on a different screen.** The
  // flag and the approver list are set by two different endpoints, so an admin
  // told only "approvers are required" would look for a field this form does
  // not have (docs/admin-plan.md §4.3).
  it('sends the admin to assign approvers before requiring approval', () => {
    const rejection = describeResourceRejection(problem(422, 'ApproversRequired'));

    expect(rejection.fieldMessages.requiresApproval).toContain('at least one approver');
    expect(rejection.fieldMessages.requiresApproval).toContain('Assign approvers first');
    expect(rejection.formMessage).toBeNull();
  });

  // Decision `0005`: a Pending booking reserves its units in full, so it holds
  // capacity before anyone has decided on it. Without that sentence the refusal
  // reads as wrong to an admin looking at a calendar of unconfirmed requests.
  it('explains that pending requests count toward the capacity floor', () => {
    const rejection = describeResourceRejection(problem(422, 'CapacityBelowExistingBookings'));

    expect(rejection.fieldMessages.capacity).toContain('Pending requests count too');
  });

  // CLAUDE.md §4.3 refuses a resolvable-but-non-canonical id on purpose
  // ("Eastern Standard Time", "america/new_york"), so the message has to point
  // at the picker rather than suggest the timezone does not exist.
  it('puts an unrecognized timezone on the timezone control', () => {
    const rejection = describeResourceRejection(problem(400, 'InvalidTimeZone'));

    expect(rejection.fieldMessages.timeZoneId).toContain('IANA');
    expect(rejection.formMessage).toBeNull();
  });

  // FR-3.5. Archiving is one-way and there is no unarchive, so this is a dead
  // end — the copy must not imply a way back.
  it('says an archived resource can no longer be edited, and that it is final', () => {
    const rejection = describeResourceRejection(problem(422, 'ResourceArchived'));

    expect(rejection.formMessage).toContain('archived');
    expect(rejection.formMessage).toContain('cannot be undone');
  });

  // `Resources` gained a RowVersion in the 2026-09-15 hardening pass, so two
  // admins saving at once is caught rather than silently last-write-wins. The
  // instruction is to reload, never to re-send — re-sending is precisely how
  // the other admin's work gets overwritten.
  it('tells the admin to reload on a concurrency conflict, not to save again', () => {
    const rejection = describeResourceRejection(problem(409, 'ConcurrencyConflict'));

    expect(rejection.formMessage).toContain('Reload');
    expect(rejection.formMessage).toContain('overwrite');
    expect(rejection.formMessage).not.toMatch(/try again/i);
  });

  // 404 switches the screen to a not-found state rather than showing a message
  // on a form for a resource that is not there. Also the answer for another
  // tenant's real id (AC-4) — the client cannot tell, and must not.
  it('flags a missing resource rather than producing a form message', () => {
    const rejection = describeResourceRejection(problem(404, 'ResourceNotFound'));

    expect(rejection.resourceNotFound).toBe(true);
    expect(rejection.formMessage).toBeNull();
  });

  // FluentValidation reports under the C# property name. Every property of
  // Create/UpdateResourceRequest maps to a control on this form, unlike the
  // booking form where the instants come from the URL.
  it('routes each validation failure to its own control', () => {
    const rejection = describeResourceRejection(
      problem(400, 'ValidationFailed', {
        Name: ['Name must not be empty.'],
        Capacity: ['Capacity must be greater than 0.'],
        MaxDurationMinutes: ['MaxDurationMinutes must be greater than or equal to MinDurationMinutes.'],
      }),
    );

    expect(rejection.fieldMessages.name).toContain('must not be empty');
    expect(rejection.fieldMessages.capacity).toContain('greater than 0');
    expect(rejection.fieldMessages.maxDurationMinutes).toContain('greater than or equal to');
    expect(rejection.formMessage).toBeNull();
  });

  it('falls back to a form message when a validation failure names a field this form has no control for', () => {
    const rejection = describeResourceRejection(
      problem(400, 'ValidationFailed', { SomethingElse: ['Nope.'] }),
    );

    expect(rejection.formMessage).toContain('not valid');
    expect(rejection.fieldMessages).toEqual({});
  });

  // Nothing on an admin path ever offers the booking screens' way out — there
  // is no slot being picked here. The flag lives on the shared shape, so this
  // is what pins that the admin dialect leaves it alone.
  it('never suggests re-checking availability', () => {
    for (const code of ['ApproversRequired', 'CapacityBelowExistingBookings', 'ResourceArchived', 'ConcurrencyConflict']) {
      expect(describeResourceRejection(problem(422, code)).recheckAvailability).toBe(false);
    }

    expect(describeResourceRejection(problem(400, 'ValidationFailed', { Whatever: ['x'] })).recheckAvailability).toBe(false);
  });

  it('reports an unreachable server as an unknown outcome', () => {
    const rejection = describeResourceRejection(new HttpErrorResponse({ status: 0, statusText: 'Unknown' }));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('may or may not have been saved');
  });

  it('treats a 5xx the same way — the save may well have landed', () => {
    const rejection = describeResourceRejection(problem(500));

    expect(rejection.outcomeUnknown).toBe(true);
    expect(rejection.formMessage).toContain('may or may not have been saved');
  });

  it('falls back to a plain refusal for an unrecognized code', () => {
    const rejection = describeResourceRejection(problem(422, 'SomeFutureRule'));

    expect(rejection.formMessage).toBe('This resource could not be saved.');
    expect(rejection.outcomeUnknown).toBe(false);
  });

  it('does not throw on something that is not an HTTP error at all', () => {
    expect(describeResourceRejection(new Error('boom')).formMessage).toBe('This resource could not be saved.');
  });
});
