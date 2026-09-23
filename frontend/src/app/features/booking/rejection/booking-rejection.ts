import {
  Rejection,
  RejectionCopy as GenericRejectionCopy,
  RejectionDialect as GenericRejectionDialect,
  describeRejection,
} from '../../../core/http/rejection';

// Every way `POST /bookings` can refuse, in one place: what to say, and where
// to say it. A map rather than a chain of throw-site-shaped `if`s, so this
// file can be read against the backend's own `ReasonCodes` catalogue
// (CLAUDE.md §6) and checked for gaps at a glance.
//
// **The shared machinery moved to `core/http/rejection.ts` in admin console
// phase 2** — it was never booking-specific, and the admin screens need it over
// a different vocabulary of fields. Nothing here changed behaviour; this file
// is now the one-off dialect, the booking field vocabulary, and the
// re-exports its three sibling dialects import.
//
// The codes this endpoint can actually return (BookingsController.Create's own
// list): SlotUnavailable, CapacityExceeded (409); ResourceArchived,
// OutsideAvailability, BlackoutPeriod, BookingDurationOutOfRange,
// BookingInThePast (422); ResourceNotFound (404); ValidationFailed (400).
// Plus two the stack can produce without the handler naming them:
// ConcurrencyConflict (409, from DbUpdateConcurrencyException) and a request
// that never reached a server at all.
//
// Codes deliberately absent: BookingNotFound / BookingNotCancellable /
// BookingNotPending belong to the cancel and approve endpoints (Phases 4 and
// 6), and NoOccurrencesCreated to the recurring path (step 7). Adding a code
// here that this endpoint cannot return would be as wrong as missing one —
// the catalogue is meant to describe what can actually arrive.

// Which control a message belongs against. Anything with no control to blame
// is a top-of-form message instead.
//
// The last five belong to the recurring half of the same screen (see
// `recurrence-rejection.ts`): one union rather than two, because `title` and
// `quantity` are shared controls and `BookingRejection` is one type either
// endpoint's refusal arrives as.
export type BookingFieldName =
  | 'title'
  | 'quantity'
  | 'duration'
  | 'startDate'
  | 'interval'
  | 'times'
  | 'occurrenceCount'
  | 'endDate'
  // The cancel dialect's only control (`cancel-rejection.ts`) — the optional
  // reason, which the server caps at 300 characters.
  | 'reason';

// The shape every booking-side dialect produces. Named separately from the
// generic `Rejection` so the four dialects and the components that render them
// keep reading in their own vocabulary.
export type BookingRejection = Rejection<BookingFieldName>;

// The three sibling dialects (`recurrence-rejection.ts`, `cancel-rejection.ts`,
// `approval-rejection.ts`) import these names from here and always have. Bound
// to the booking field union rather than re-exported raw, so they stay the
// zero-argument types those files already spell — and so, inside this feature,
// "a dialect" means a dialect over *booking* controls and cannot accidentally
// be written over someone else's. A dialect for a different feature imports
// from `core/http/rejection.ts` directly and binds its own.
export type RejectionCopy = GenericRejectionCopy<BookingFieldName>;
export type RejectionDialect = GenericRejectionDialect<BookingFieldName>;
export { describeRejection } from '../../../core/http/rejection';

// Reason code -> message and placement. Wording follows the code's own
// meaning in `ReasonCodes`, not a paraphrase of the HTTP status: in
// particular SlotUnavailable and CapacityExceeded split by *what is left*
// (CLAUDE.md §6), so only the second mentions quantity. It is not confined to
// pooled resources: a request for more units than a one-unit resource has is
// well-formed (the validator leaves Quantity unbounded) and comes back
// CapacityExceeded — which is why the form refuses such a quantity itself,
// before the request goes out.
const REJECTION_COPY: Record<string, RejectionCopy> = {
  // 409s: the slot genuinely moved between the availability query and the
  // submit, so looking again is worth doing.
  SlotUnavailable: {
    message: 'Someone booked this time while you were filling in the form. Check availability to pick another slot.',
    recheckAvailability: true,
  },
  CapacityExceeded: {
    message:
      'Fewer units are free for this time than you asked for. Check availability to see how many are left, or book a smaller quantity.',
    recheckAvailability: true,
  },
  ConcurrencyConflict: {
    message: 'This resource changed while your booking was being created. Check availability and try again.',
    recheckAvailability: true,
  },

  // 422s: a rule refused it, and re-checking availability cannot change that,
  // so none of these offer the action.
  ResourceArchived: {
    message: 'This resource has been archived, so it can no longer be booked.',
  },
  BlackoutPeriod: {
    message: 'This time falls inside a blackout period, so it cannot be booked.',
  },
  OutsideAvailability: {
    message: "This time is outside the resource's bookable hours.",
  },
  BookingInThePast: {
    message: 'This time has already passed. Pick a time in the future.',
  },

  // The one 422 with a control to blame.
  BookingDurationOutOfRange: {
    field: 'duration',
    message: "This booking's length is outside what the resource allows.",
  },
};

// FluentValidation reports failures under the C# property name ("Title",
// "Quantity" — PascalCase, as `problem-details.ts` records), never the form's
// own control names. One translation, here, rather than one at every reader —
// the convention `LoginComponent.BACKEND_FIELD_NAMES` established.
//
// StartsAtUtc / EndsAtUtc / ResourceId are deliberately *not* here: this form
// has no control for any of them (they come from the URL), so a failure on one
// is a top-of-form message with a way back to pick a slot again, not a message
// pointed at a field the member cannot see.
const BACKEND_FIELD_NAMES: Record<string, BookingFieldName> = {
  Title: 'title',
  Quantity: 'quantity',
};

const INVALID_SELECTION_MESSAGE =
  'The selected time is not valid. Go back to availability and pick a slot again.';

const GENERIC_MESSAGE = 'This booking could not be created.';

const UNKNOWN_OUTCOME_MESSAGE =
  'Your booking may have been created — we could not confirm it. Check your calendar on that date before trying again, so you do not book the same time twice.';

const ONE_OFF_DIALECT: RejectionDialect = {
  copy: REJECTION_COPY,
  backendFields: BACKEND_FIELD_NAMES,
  unmappedField: { message: INVALID_SELECTION_MESSAGE, recheckAvailability: true },
  genericMessage: GENERIC_MESSAGE,
  unknownOutcomeMessage: UNKNOWN_OUTCOME_MESSAGE,
};

export function describeBookingRejection(error: unknown): BookingRejection {
  return describeRejection(error, ONE_OFF_DIALECT);
}
