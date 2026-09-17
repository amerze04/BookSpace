import { HttpErrorResponse } from '@angular/common/http';
import { isProblemDetails } from '../../core/http/problem-details';

// Every way `POST /bookings` can refuse, in one place: what to say, and where
// to say it. A map rather than a chain of throw-site-shaped `if`s, so this
// file can be read against the backend's own `ReasonCodes` catalogue
// (CLAUDE.md §6) and checked for gaps at a glance.
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
export type BookingFieldName = 'title' | 'quantity' | 'duration';

export interface BookingRejection {
  // Shown above the submit button. Null only when every message this rejection
  // carries is field-level.
  formMessage: string | null;

  // Offer a way back to the availability screen. True only when re-checking
  // could genuinely change the answer — the slot moved — and false when it
  // cannot, so the action never suggests that trying again might work.
  recheckAvailability: boolean;

  // The one-off idempotency gap surfacing in the UI (wp7-plan.md §7): the
  // request may have reached `dbo.CreateBooking` and committed even though no
  // usable response came back, and `POST /bookings` has no idempotency key to
  // make a retry safe. So the member is told the booking *may* exist and sent
  // to check — never offered a retry button that could double it.
  mayHaveBeenCreated: boolean;

  // Per-control messages, keyed by the form's own field names.
  fieldMessages: Partial<Record<BookingFieldName, string>>;

  // The resource itself is gone or was never visible to this caller — the
  // screen switches to step 2's own not-found state rather than showing a
  // message on a form for a resource that isn't there.
  resourceNotFound: boolean;
}

interface RejectionCopy {
  message: string;
  field?: BookingFieldName;
  recheckAvailability?: boolean;
}

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

// A request that reached no server, or one whose outcome the server could not
// report. Both share the same honest answer: we do not know whether the
// booking exists.
const UNKNOWN_OUTCOME_MESSAGE =
  'Your booking may have been created — we could not confirm it. Check My Bookings before trying again, so you do not book the same time twice.';

export function describeBookingRejection(error: unknown): BookingRejection {
  if (!(error instanceof HttpErrorResponse)) {
    return formOnly(GENERIC_MESSAGE);
  }

  // **Checked before any reason-code branch**, exactly as `handleLoginError`
  // does: status 0 means the request never reached a server (offline, DNS,
  // CORS, refused connection), and `HttpErrorResponse.error` is then a
  // ProgressEvent — not a ProblemDetails — so reading a reason code off it
  // would find nothing and fall through to a message that claims more than is
  // known.
  if (error.status === 0) {
    return { ...formOnly(UNKNOWN_OUTCOME_MESSAGE), mayHaveBeenCreated: true };
  }

  // A 5xx *did* reach the server, so the booking may well have been committed
  // before whatever failed — same unknown outcome, same advice. This is why
  // there is no "try again" button anywhere on this path.
  if (error.status >= 500) {
    return { ...formOnly(UNKNOWN_OUTCOME_MESSAGE), mayHaveBeenCreated: true };
  }

  if (!isProblemDetails(error.error)) {
    return formOnly(GENERIC_MESSAGE);
  }

  const problem = error.error;

  if (problem.reasonCode === 'ResourceNotFound') {
    return { ...formOnly(null), resourceNotFound: true };
  }

  if (problem.errors) {
    return fromValidationErrors(problem.errors);
  }

  const copy = REJECTION_COPY[problem.reasonCode];
  if (!copy) {
    // An unrecognized code is still a refusal, and saying so plainly beats
    // inventing an explanation for a rule this client does not know about.
    return formOnly(GENERIC_MESSAGE);
  }

  if (copy.field) {
    return { ...empty(), fieldMessages: { [copy.field]: copy.message } };
  }

  return { ...formOnly(copy.message), recheckAvailability: copy.recheckAvailability ?? false };
}

function fromValidationErrors(errors: Record<string, string[]>): BookingRejection {
  const fieldMessages: Partial<Record<BookingFieldName, string>> = {};
  let hasUnmappedField = false;

  for (const [backendField, messages] of Object.entries(errors)) {
    const field = BACKEND_FIELD_NAMES[backendField];
    if (field && messages.length > 0) {
      fieldMessages[field] = messages[0];
    } else {
      hasUnmappedField = true;
    }
  }

  return {
    ...empty(),
    fieldMessages,
    // A failure on a field this form doesn't render (the instants, the
    // resource id) can only have come from the URL, so the way out is to pick
    // a slot again rather than to edit anything here.
    formMessage: hasUnmappedField ? INVALID_SELECTION_MESSAGE : null,
    recheckAvailability: hasUnmappedField,
  };
}

function empty(): BookingRejection {
  return {
    formMessage: null,
    recheckAvailability: false,
    mayHaveBeenCreated: false,
    fieldMessages: {},
    resourceNotFound: false,
  };
}

function formOnly(message: string | null): BookingRejection {
  return { ...empty(), formMessage: message };
}
