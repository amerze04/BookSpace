namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (WP-5 Phase 3, FR-7.1-7.5, AC-5). Approve or reject
// called on a booking that is not (or no longer) Pending.
//
// The one genuinely new code Phase 3 adds — every other rejection an approval
// decision can produce (SlotUnavailable, CapacityExceeded, BlackoutPeriod,
// ResourceArchived, ResourceNotFound) reuses WP-4's existing codes and
// exception subclasses unchanged, since dbo.ApproveBooking inherits decision
// 0023's design whole.
public sealed class BookingNotPendingException : AppException
{
    public BookingNotPendingException(Guid bookingId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.BookingNotPending,
            $"Booking {bookingId} is not (or no longer) Pending.")
    {
    }
}
