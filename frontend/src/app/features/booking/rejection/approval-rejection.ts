import {
  BookingFieldName,
  BookingRejection,
  RejectionCopy,
  RejectionDialect,
  describeRejection,
} from './booking-rejection';

// Every way an approval decision can be refused, in an approver's vocabulary.
// A fourth dialect on `booking-rejection.ts`'s machinery rather than a second
// mapper — the status-0/5xx unknown-outcome rules and the validation walk are
// identical for every write this feature makes, and only the words differ.
//
// The codes, read off `BookingsController.Approve` / `.Reject` and their
// validators rather than inferred:
//   404 BookingNotFound · 422 BookingNotPending · 400 ValidationFailed (an
//   over-long note) · and, on approve only, 409 SlotUnavailable /
//   CapacityExceeded and 422 BlackoutPeriod / ResourceArchived from
//   dbo.ApproveBooking's re-check.
//
// **Approve and reject share one dialect even though only approve can produce
// the capacity codes.** A code a reject can never return simply never arrives,
// and two near-identical maps that had to be kept in step would be the more
// likely source of a wrong message than one map with an unreachable entry.

// The since-taken-slot outcome, AC-5's whole point, shared by both codes below
// because a decision on a slot that is gone is one event to an approver however
// the procedure classified what was left.
//
// **It must never read as a generic failure.** This is the arm that proves the
// approval re-check is real, and an approver who saw "something went wrong"
// would reasonably conclude the system was broken rather than that the slot
// went while the request sat in their queue.
const SLOT_TAKEN_MESSAGE =
  'This slot was taken while the request was waiting, so it can no longer be approved. '
  + 'Nothing has been decided — the request is still pending, and rejecting it is still '
  + 'possible.';

const DECISION_COPY: Record<string, RejectionCopy> = {
  // The concurrent-decision case and the already-decided case are the same
  // code, and the client cannot tell them apart — nor does it need to. Either
  // way this approver's decision did not land, someone else's did, and the
  // honest instruction is to look rather than to try again.
  BookingNotPending: {
    message:
      'This request has already been decided — either by someone else, or because it was '
      + 'cancelled or expired. Reload to see where it stands.',
  },

  // 404 here is the same byte-identical answer every booking read gives: not
  // this caller's to see. For an approver it most likely means the booking left
  // their reach — the resource stopped listing them as an approver, or it was
  // removed — between loading the queue and deciding.
  BookingNotFound: {
    message: "This request is no longer available to you, so it couldn't be decided.",
  },

  // AC-5, decided under dbo.ApproveBooking's UPDLOCK/HOLDLOCK — the approval
  // re-runs the capacity check, so approving is a real decision at the moment
  // it is made rather than a formality over one already taken.
  SlotUnavailable: { message: SLOT_TAKEN_MESSAGE },
  CapacityExceeded: { message: SLOT_TAKEN_MESSAGE },

  // Re-checked under the same lock. Decision `0001` gives a blackout absolute
  // priority, so a booking inside one must never be confirmed — approving into
  // a blackout that appeared after the request was made is exactly the race
  // that check exists for.
  BlackoutPeriod: {
    message:
      'The resource has been blocked out over this time since the request was made, so it '
      + 'cannot be approved. Rejecting it is still possible.',
  },

  ResourceArchived: {
    message:
      'This resource has been archived since the request was made, so the booking cannot be '
      + 'confirmed. Rejecting the request is still possible.',
  },
};

// FluentValidation reports under the C# property name. Both command validators
// have exactly two properties and only one is a control an approver types into
// — `BookingId` comes from the route.
//
// **`Note` maps onto `reason`**, which is the cancel dialect's control name and
// the only free-text field `BookingFieldName` carries. Reusing it rather than
// widening the union: both are "the optional words an actor attaches to a
// decision", they are never on screen at the same time, and a second name for
// the same kind of control would have to be threaded through every consumer of
// `BookingRejection` for no gain. The server caps this one at 500 and the
// cancel's at 300, which is a bound the form applies, not something this
// mapping has to know.
const DECISION_BACKEND_FIELDS: Record<string, BookingFieldName> = {
  Note: 'reason',
};

const DECISION_DIALECT: RejectionDialect = {
  copy: DECISION_COPY,
  backendFields: DECISION_BACKEND_FIELDS,

  unmappedField: {
    message: "The decision couldn't be recorded — the request wasn't valid.",
    recheckAvailability: false,
  },

  genericMessage: 'This request could not be decided.',

  // **No retry is ever offered on this path**, which is why the wording sends
  // the approver to look rather than to try again. A decision is deliberately
  // not idempotent — a second call answers 422 BookingNotPending — and on the
  // approve side a repeat could also lose the capacity race a second time.
  unknownOutcomeMessage:
    'Your decision may or may not have been recorded — we could not confirm it. Reload the '
    + 'request to see where it stands rather than deciding again.',
};

export function describeDecisionRejection(error: unknown): BookingRejection {
  return describeRejection(error, DECISION_DIALECT);
}
