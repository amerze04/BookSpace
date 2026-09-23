import { HttpErrorResponse } from '@angular/common/http';
import { isProblemDetails } from './problem-details';

// Turning a refused write into something a screen can render: what to say, and
// where to say it.
//
// **Extracted here in admin console phase 2**, from
// `features/booking/rejection/booking-rejection.ts`, where it was built during
// WP-7 and grew four dialects (one-off booking, recurrence, cancel, approval
// decision). Nothing about it was ever booking-specific — it reads a
// ProblemDetails (CLAUDE.md §6, decisions/0016) and maps a reason code onto
// copy — and the admin console needs the same machinery over an entirely
// different vocabulary of fields. `core/http` is where it belongs: it is the
// other half of `problem-details.ts`, which describes the contract this
// interprets.
//
// Two things changed in the move and nothing else did:
//   - The field-name type is a generic parameter rather than the booking form's
//     own union, so an admin form can key messages by `name` / `capacity` /
//     `timeZoneId` without those leaking into the booking screens' type.
//   - `describeRejection` is generic accordingly.
// The four existing dialects still import from `booking-rejection.ts` and are
// untouched — their tests are the proof that this move changed no behaviour.

export interface Rejection<TField extends string> {
  // Shown above the submit button. Null only when every message this rejection
  // carries is field-level.
  formMessage: string | null;

  // Offer a way back to the availability screen. True only when re-checking
  // could genuinely change the answer — the slot moved — and false when it
  // cannot, so the action never suggests that trying again might work.
  //
  // Booking-specific in practice: an admin dialect leaves it false, because
  // there is no slot involved in renaming a room. It stays on the shared shape
  // rather than being split out because the alternative is two near-identical
  // result types, and a consumer that ignores one boolean costs nothing.
  recheckAvailability: boolean;

  // **The write may have landed even though no usable answer came back** — the
  // request reached no server (status 0) or the server could not report what
  // happened (5xx). Whatever the write was, the user is told the outcome is
  // unknown and sent to check.
  //
  // Whether that is followed by "and here is a retry" is the dialect's call,
  // not this flag's: for a non-idempotent write (creating a booking, deciding a
  // request) a retry could double it, while `POST /resources/{id}/archive` is
  // deliberately idempotent and safe to repeat.
  outcomeUnknown: boolean;

  // Per-control messages, keyed by the form's own field names.
  fieldMessages: Partial<Record<TField, string>>;

  // The resource itself is gone or was never visible to this caller — the
  // screen switches to a not-found state rather than showing a message on a
  // form for a resource that isn't there.
  resourceNotFound: boolean;
}

export interface RejectionCopy<TField extends string> {
  message: string;
  field?: TField;
  recheckAvailability?: boolean;
}

// Everything that differs between endpoints. The *machinery* — the status-0/5xx
// unknown-outcome rules, ResourceNotFound, the validation-error walk — is
// identical for all of them and lives once, below; the vocabulary is not, and
// pretending otherwise is what WP-7's frontend hardening finding 2 was about: a
// series refused for `OccurrenceCount` was being told to "go back to
// availability and pick a slot again", which is neither where the problem is
// nor a screen that could fix it.
export interface RejectionDialect<TField extends string> {
  // Reason code -> message and placement.
  copy: Record<string, RejectionCopy<TField>>;

  // FluentValidation's C# property name -> the form control that holds it.
  backendFields: Record<string, TField>;

  // What to say when a validation failure names a field this form has no
  // control for, and whether re-checking availability is the way out of it.
  unmappedField: { message: string; recheckAvailability: boolean };

  // A refusal this client has no copy for. Still a refusal; saying so plainly
  // beats inventing an explanation for a rule it does not know about.
  genericMessage: string;

  // A request that reached no server, or one whose outcome the server could
  // not report. Both share the same honest answer: we do not know whether it
  // landed.
  unknownOutcomeMessage: string;
}

export function describeRejection<TField extends string>(
  error: unknown,
  dialect: RejectionDialect<TField>,
): Rejection<TField> {
  if (!(error instanceof HttpErrorResponse)) {
    return formOnly<TField>(dialect.genericMessage);
  }

  // **Checked before any reason-code branch**, exactly as `handleLoginError`
  // does: status 0 means the request never reached a server (offline, DNS,
  // CORS, refused connection), and `HttpErrorResponse.error` is then a
  // ProgressEvent — not a ProblemDetails — so reading a reason code off it
  // would find nothing and fall through to a message that claims more than is
  // known.
  if (error.status === 0) {
    return { ...formOnly<TField>(dialect.unknownOutcomeMessage), outcomeUnknown: true };
  }

  // A 5xx *did* reach the server, so the write may well have been committed
  // before whatever failed — same unknown outcome, same advice.
  if (error.status >= 500) {
    return { ...formOnly<TField>(dialect.unknownOutcomeMessage), outcomeUnknown: true };
  }

  if (!isProblemDetails(error.error)) {
    return formOnly<TField>(dialect.genericMessage);
  }

  const problem = error.error;

  if (problem.reasonCode === 'ResourceNotFound') {
    return { ...formOnly<TField>(null), resourceNotFound: true };
  }

  if (problem.errors) {
    return fromValidationErrors(problem.errors, dialect);
  }

  const copy = dialect.copy[problem.reasonCode];
  if (!copy) {
    // An unrecognized code is still a refusal, and saying so plainly beats
    // inventing an explanation for a rule this client does not know about.
    return formOnly<TField>(dialect.genericMessage);
  }

  if (copy.field) {
    return { ...empty<TField>(), fieldMessages: { [copy.field]: copy.message } as Partial<Record<TField, string>> };
  }

  return { ...formOnly<TField>(copy.message), recheckAvailability: copy.recheckAvailability ?? false };
}

function fromValidationErrors<TField extends string>(
  errors: Record<string, string[]>,
  dialect: RejectionDialect<TField>,
): Rejection<TField> {
  const fieldMessages: Partial<Record<TField, string>> = {};
  let hasUnmappedField = false;

  for (const [backendField, messages] of Object.entries(errors)) {
    const field = dialect.backendFields[backendField];
    if (field && messages.length > 0) {
      fieldMessages[field] = messages[0];
    } else {
      hasUnmappedField = true;
    }
  }

  return {
    ...empty<TField>(),
    fieldMessages,
    // A failure on a field this form doesn't render is not something to point
    // at a control — what the way out is differs per endpoint, which is why the
    // dialect owns both halves of it.
    formMessage: hasUnmappedField ? dialect.unmappedField.message : null,
    recheckAvailability: hasUnmappedField && dialect.unmappedField.recheckAvailability,
  };
}

function empty<TField extends string>(): Rejection<TField> {
  return {
    formMessage: null,
    recheckAvailability: false,
    outcomeUnknown: false,
    fieldMessages: {},
    resourceNotFound: false,
  };
}

function formOnly<TField extends string>(message: string | null): Rejection<TField> {
  return { ...empty<TField>(), formMessage: message };
}
