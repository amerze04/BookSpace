import {
  BookingFieldName,
  BookingRejection,
  RejectionCopy,
  RejectionDialect,
  describeRejection,
} from './booking-rejection';

// Every way `POST /bookings/{id}/cancel` can refuse, in a cancelling member's
// vocabulary. A third dialect on `booking-rejection.ts`'s machinery rather than
// a second mapper — the status-0/5xx unknown-outcome rules and the validation
// walk are identical for every write this feature makes; only the words differ.
//
// The codes this endpoint can actually return, read off `BookingsController
// .Cancel` and `CancelBookingCommandRequestValidator`:
//   404 BookingNotFound · 422 BookingNotCancellable · 409 ConcurrencyConflict ·
//   400 ValidationFailed (an over-long reason).
//
// **Codes deliberately absent**: every capacity and availability code the
// create path carries. Cancelling releases a claim rather than making one, so
// there is nothing for `dbo.CreateBooking`'s lock to refuse — which is also
// why this endpoint has no 409 from the procedure, only the optimistic-
// concurrency one `RowVersion` produces.
const CANCEL_COPY: Record<string, RejectionCopy> = {
  // The two halves `ReasonCodes.BookingNotCancellable` has always described
  // (`Booking.CanBeCancelled`): already terminal, or already ended. The message
  // names both because the client cannot tell which applies — and either way
  // the screen's own copy of the rule has just been proven stale, so the advice
  // is to look again rather than to try again.
  BookingNotCancellable: {
    message:
      'This booking can no longer be cancelled — it has either already ended or already been '
      + 'cancelled. Reload to see where it stands.',
  },

  // 404 here is the same byte-identical answer the detail read gives: another
  // member's booking, another tenant's, or one that exists nowhere. It can also
  // mean the booking was removed between loading this screen and acting on it.
  BookingNotFound: {
    message: "This booking is no longer available to you, so it couldn't be cancelled.",
  },

  // Two people cancelling at once — `RowVersion` picks one (CLAUDE.md §5). The
  // loser's booking *is* cancelled, just not by them, so the honest instruction
  // is to look rather than to retry: a retry would answer BookingNotCancellable
  // anyway, having already lost the race.
  ConcurrencyConflict: {
    message:
      'Someone else changed this booking at the same moment. Reload to see its current state.',
  },
};

// FluentValidation reports under the C# property name.
// `CancelBookingCommandRequest` has exactly two, and only one of them is a
// control a member can edit — `BookingId` comes from the route.
const CANCEL_BACKEND_FIELDS: Record<string, BookingFieldName> = {
  Reason: 'reason',
};

const CANCEL_DIALECT: RejectionDialect = {
  copy: CANCEL_COPY,
  backendFields: CANCEL_BACKEND_FIELDS,

  // `BookingId` is the only other field this command has, and it comes from the
  // route rather than from anything the member typed — so a validation failure
  // naming it is a malformed URL, not a correctable input. Re-checking
  // availability is meaningless either way: nothing here is asking for a slot.
  unmappedField: {
    message: "This booking couldn't be cancelled — the request wasn't valid.",
    recheckAvailability: false,
  },

  genericMessage: 'This booking could not be cancelled.',

  // **The same shape as the create path's unknown outcome, for a different
  // reason.** There the danger is creating a second booking; here it is that a
  // second cancel would overwrite `CancelledByUserId`, `CancelledAtUtc` and the
  // reason with a second actor's — the record of who called the meeting off
  // would quietly change. `POST .../cancel` is deliberately not idempotent
  // (`CancelBookingCommandRequest`'s own header), so there is no retry button
  // on this path either.
  unknownOutcomeMessage:
    'Your cancellation may or may not have gone through — we could not confirm it. Reload this '
    + 'booking to see where it stands rather than trying again.',
};

export function describeCancelRejection(error: unknown): BookingRejection {
  return describeRejection(error, CANCEL_DIALECT);
}
