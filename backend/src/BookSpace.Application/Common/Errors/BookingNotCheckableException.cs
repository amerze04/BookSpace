namespace BookSpace.Application.Common.Errors;

// ErrorKind.Conflict (WP-8 Phase 3, docs/wp8-plan.md decision D5). Check-in
// called on a booking that is not currently Confirmed — Pending (never
// started), Cancelled, Rejected, Completed, or already released as a
// NoShow.
//
// Conflict rather than RuleViolation, unlike BookingNotCancellable: nothing
// here is a rule the request violated, it is a fact about the booking's
// current state that a fresh GET would already show — the same reasoning
// that makes EmailAlreadyInUse a Conflict rather than a Validation failure.
public sealed class BookingNotCheckableException : AppException
{
    public BookingNotCheckableException(Guid bookingId)
        : base(
            ErrorKind.Conflict,
            ReasonCodes.BookingNotCheckable,
            $"Booking {bookingId} is not currently Confirmed, and cannot be checked in.")
    {
    }
}
