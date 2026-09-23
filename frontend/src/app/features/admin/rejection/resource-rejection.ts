import { Rejection, RejectionCopy, RejectionDialect, describeRejection } from '../../../core/http/rejection';

// Every way the resource write endpoints can refuse, in an administrator's
// vocabulary. The admin console's first dialect over `core/http/rejection.ts`'s
// machinery — a fifth alongside the booking screens' four — rather than a
// second error-handling scheme.
//
// It covers all three resource writes, because they share a vocabulary and an
// audience:
//   `POST /resources`            create
//   `PUT /resources/{id}`        edit
//   `POST /resources/{id}/archive`
//
// The codes, read off `ResourcesController` and `ResourceWriteRules` rather
// than inferred:
//   400 InvalidTimeZone · 400 ValidationFailed · 404 ResourceNotFound ·
//   409 ConcurrencyConflict (global, from DbUpdateConcurrencyException —
//   `Resources` gained its own RowVersion in the 2026-09-15 hardening pass) ·
//   422 ResourceArchived · 422 CapacityBelowExistingBookings ·
//   422 ApproversRequired.
//
// Deliberately absent: `ApproverNotEligible` and `OverlappingAvailabilityWindow`
// belong to the approvers and availability-window endpoints (phases 5 and 4),
// which get their own dialects. A code this form cannot receive would be as
// wrong here as a missing one — the catalogue is meant to describe what can
// actually arrive.

// Which control a message belongs against, keyed by the admin form's own
// control names rather than the booking screens' union — which is the whole
// reason the machinery became generic in phase 2.
export type ResourceFieldName =
  | 'name'
  | 'description'
  | 'resourceType'
  | 'capacity'
  | 'timeZoneId'
  | 'requiresApproval'
  | 'minDurationMinutes'
  | 'maxDurationMinutes';

export type ResourceRejection = Rejection<ResourceFieldName>;

// `ApproversRequired` was here and was **removed by decision 0028**, along with
// the reason code itself. A resource may now require approval with an empty
// approver list — that is what one looks like between being created gated and
// having its approvers assigned — so nothing can return the code and copy for
// it would describe a rule the server no longer has.
const RESOURCE_COPY: Record<string, RejectionCopy<ResourceFieldName>> = {
  // Counts Pending as well as Confirmed: a pending request reserves its units
  // in full (decision `0005`), so it is holding capacity even before anyone has
  // decided on it. Worth saying, because an admin looking at a half-empty
  // calendar will otherwise read the refusal as wrong.
  CapacityBelowExistingBookings: {
    field: 'capacity',
    message:
      'Existing bookings already use more units than this. Pending requests count too — they '
      + 'hold their units until they are decided.',
  },

  // Not merely "a timezone we could not resolve": CLAUDE.md §4.3 requires a
  // canonical IANA id, so `Eastern Standard Time` and `america/new_york` are
  // refused on purpose even though the host can resolve both.
  InvalidTimeZone: {
    field: 'timeZoneId',
    message: 'Pick a timezone from the list — the value sent was not a recognized IANA timezone id.',
  },

  // FR-3.5. Archiving is one-way, so this is a dead end rather than something
  // to retry; the copy says so instead of implying a way back.
  ResourceArchived: {
    message:
      'This resource has been archived, so it can no longer be edited. Archiving cannot be undone.',
  },

  // `Resources` carries a RowVersion, so two admins saving the same resource at
  // once is caught rather than silently last-write-wins. Reloading is the only
  // honest instruction: this form's values are now built on a version that no
  // longer exists, and re-sending them would overwrite whatever the other admin
  // just did.
  ConcurrencyConflict: {
    message:
      'Someone else changed this resource while you were editing it. Reload to see their changes '
      + 'before saving yours — saving now would overwrite them.',
  },
};

// FluentValidation reports under the C# property name (`problem-details.ts`
// records the PascalCase shape). One translation, here, rather than one at
// every reader.
//
// Every property of CreateResourceRequest/UpdateResourceRequest is mapped,
// because unlike the booking form this one renders a control for all of them —
// nothing comes from the URL except the id, which is not a field.
const RESOURCE_BACKEND_FIELDS: Record<string, ResourceFieldName> = {
  Name: 'name',
  Description: 'description',
  ResourceType: 'resourceType',
  Capacity: 'capacity',
  TimeZoneId: 'timeZoneId',
  RequiresApproval: 'requiresApproval',
  MinDurationMinutes: 'minDurationMinutes',
  MaxDurationMinutes: 'maxDurationMinutes',
};

const RESOURCE_DIALECT: RejectionDialect<ResourceFieldName> = {
  copy: RESOURCE_COPY,
  backendFields: RESOURCE_BACKEND_FIELDS,

  // Every field this form sends has a control, so a validation failure naming
  // something else means the client and the server have drifted apart. Saying
  // that plainly beats pointing at a control that is not the problem.
  unmappedField: {
    message: 'The resource could not be saved — one of the values sent was not valid.',
    recheckAvailability: false,
  },

  genericMessage: 'This resource could not be saved.',

  // **A retry is safe here, and that is unusual enough to state.** Every write
  // on this dialect is idempotent in the sense that matters: create is the one
  // exception, and the copy therefore sends the admin to look rather than to
  // resubmit. `PUT /resources/{id}` sends a full representation, so repeating
  // it converges on the same row; `POST /resources/{id}/archive` returns early
  // when the resource is already archived, deliberately, so a repeat is a no-op
  // that does not even move UpdatedAtUtc.
  //
  // The message is still "go and look", because this dialect cannot tell which
  // of the three writes produced the failure, and the one that is unsafe to
  // repeat is the one that would create a duplicate resource.
  unknownOutcomeMessage:
    'Your changes may or may not have been saved — we could not confirm it. Reload the resource '
    + 'list to see where it stands before trying again.',
};

export function describeResourceRejection(error: unknown): ResourceRejection {
  return describeRejection(error, RESOURCE_DIALECT);
}
