import { Rejection, RejectionCopy, RejectionDialect, describeRejection } from '../../../core/http/rejection';

// Every way `PUT /resources/{id}/approvers` can refuse, in an administrator's
// vocabulary. The seventh dialect over `core/http/rejection.ts`'s machinery.
//
// The codes, read off `ResourcesController.ReplaceApprovers` and
// `ReplaceApproversCommandRequestHandler` rather than inferred:
//   400 ValidationFailed · 404 ResourceNotFound · 422 ResourceArchived ·
//   422 ApproverNotEligible · 409 ConcurrencyConflict (global).
//
// `ApproversRequired` is **not** here: decision 0028 deleted both the rule and
// the code, so an empty approver list is now accepted on a gated resource.

// No per-control routing: this screen's controls are tick boxes for people, and
// a refusal names a *user id*, which is not a field. Everything lands above the
// list. The union is declared because `Rejection` is generic over it.
export type ApproverFieldName = 'approvers';

export type ApproverRejection = Rejection<ApproverFieldName>;

const APPROVER_COPY: Record<string, RejectionCopy<ApproverFieldName>> = {
  // **The one this screen is built to make unreachable, and cannot quite.**
  //
  // The picker only ever offers people `GET /users` returned, so every id it
  // sends was eligible when the page loaded. Reaching this means somebody was
  // deactivated, had their role removed, or left the tenant in the meantime.
  //
  // The message cannot say *which* person or *why* — decision `0018` collapses
  // all three causes into one code deliberately, because naming the cause would
  // confirm that a cross-tenant id exists somewhere (AC-4). So it says what is
  // true and useful instead: something changed, reload to see what.
  ApproverNotEligible: {
    message:
      'One of the people you selected can no longer approve for this organization — they may '
      + 'have been deactivated or had their role changed since this page loaded. Reload to see '
      + 'who is still available.',
  },

  // FR-3.5. A dead end rather than something to retry.
  ResourceArchived: {
    message:
      'This resource has been archived, so its approvers can no longer be changed. Archiving '
      + 'cannot be undone.',
  },

  ConcurrencyConflict: {
    message:
      'Someone else changed this resource while you were editing its approvers. Reload before '
      + 'saving — saving now would replace their list with yours.',
  },
};

// `ApproverUserIds` is the only property the command carries besides the route
// id, and a failure on it means the array itself was malformed rather than a
// particular person being wrong. Left unmapped so it reads as a form message;
// there is no control to point at.
const APPROVER_BACKEND_FIELDS: Record<string, ApproverFieldName> = {};

const APPROVER_DIALECT: RejectionDialect<ApproverFieldName> = {
  copy: APPROVER_COPY,
  backendFields: APPROVER_BACKEND_FIELDS,

  unmappedField: {
    message: "The approvers couldn't be saved — the selection wasn't valid.",
    recheckAvailability: false,
  },

  genericMessage: 'These approvers could not be saved.',

  // Safe to retry, for the same reason the availability editor's is:
  // replace-the-set is idempotent by construction, so sending the same
  // selection twice produces the same list.
  unknownOutcomeMessage:
    'Your changes may or may not have been saved — we could not confirm it. Reload to see which, '
    + 'or simply save again: sending the same selection twice is harmless.',
};

export function describeApproverRejection(error: unknown): ApproverRejection {
  return describeRejection(error, APPROVER_DIALECT);
}
