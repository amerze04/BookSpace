import { Rejection, RejectionCopy, RejectionDialect, describeRejection } from '../../../core/http/rejection';

// Every way `PUT /resources/{id}/availability-windows` can refuse, in an
// administrator's vocabulary. The sixth dialect over
// `core/http/rejection.ts`'s machinery.
//
// Its own dialect rather than a reuse of `resource-rejection.ts`, for the
// reason the booking screens keep four: the codes barely overlap, and the one
// that matters here — `OverlappingAvailabilityWindow` — has no counterpart on
// the resource form at all. A shared map would have to carry both endpoints'
// codes and would then describe refusals neither can produce.
//
// The codes, read off `ResourcesController.ReplaceAvailabilityWindows` and its
// validator rather than inferred:
//   400 ValidationFailed · 404 ResourceNotFound · 422 ResourceArchived ·
//   409 OverlappingAvailabilityWindow · 409 ConcurrencyConflict (global, from
//   DbUpdateConcurrencyException).

// This editor has no per-control message routing: a refusal names a *window*,
// and windows are rows the client invented keys for, so nothing the server
// says can be pointed at one. Everything lands above the form. The union is
// declared anyway because `Rejection` is generic over it.
export type AvailabilityFieldName = 'windows';

export type AvailabilityRejection = Rejection<AvailabilityFieldName>;

const AVAILABILITY_COPY: Record<string, RejectionCopy<AvailabilityFieldName>> = {
  // **The one this editor is mostly built to avoid.** `validateRows` applies
  // the same rule client-side and refuses to submit, so reaching this means
  // either the two rules have drifted or something outside this screen sent the
  // request. Worth wording precisely anyway: a 409 an admin cannot explain is
  // worse than one that says what the server compared.
  //
  // Adjacency is deliberately not overlap on either side — `ClosesAt` is
  // exclusive, so 09:00-12:00 and 12:00-17:00 coexist.
  OverlappingAvailabilityWindow: {
    message:
      'Two windows on the same day overlap. Each window has to finish before the next one starts '
      + '— a window that ends exactly when the next begins is fine.',
  },

  // FR-3.5: an archived resource accepts no edits. A dead end rather than
  // something to retry, and the copy says so.
  ResourceArchived: {
    message:
      'This resource has been archived, so its opening hours can no longer be changed. Archiving '
      + 'cannot be undone.',
  },

  ConcurrencyConflict: {
    message:
      'Someone else changed this resource while you were editing its hours. Reload before saving '
      + '— saving now would replace their schedule with yours.',
  },
};

// FluentValidation reports per-window failures under an indexed property name
// (`Windows[2].ClosesAt`), which names a position in the array this screen
// sorted before sending — so it cannot be resolved back to the row an admin is
// looking at. Deliberately left unmapped: the dialect says plainly that a
// window was refused rather than pointing at the wrong one.
const AVAILABILITY_BACKEND_FIELDS: Record<string, AvailabilityFieldName> = {};

const AVAILABILITY_DIALECT: RejectionDialect<AvailabilityFieldName> = {
  copy: AVAILABILITY_COPY,
  backendFields: AVAILABILITY_BACKEND_FIELDS,

  unmappedField: {
    message:
      "The schedule couldn't be saved — one of the windows wasn't valid. Check that every window "
      + 'closes after it opens.',
    recheckAvailability: false,
  },

  genericMessage: 'This schedule could not be saved.',

  // **Safe to retry, and this is the one admin write where that is plainly
  // true.** Replace-the-set is idempotent by construction: sending the same
  // schedule twice produces the same schedule. So the copy can say "try again"
  // without the hedging the booking and decision dialects need.
  unknownOutcomeMessage:
    'Your schedule may or may not have been saved — we could not confirm it. Reload to see which, '
    + 'or simply save again: sending the same schedule twice is harmless.',
};

export function describeAvailabilityRejection(error: unknown): AvailabilityRejection {
  return describeRejection(error, AVAILABILITY_DIALECT);
}
