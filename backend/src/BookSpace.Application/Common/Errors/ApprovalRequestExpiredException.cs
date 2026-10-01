namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (hardening pass, finding 3, FR-9.3). Approve or
// reject called after the request's own configured deadline
// (ApprovalRequest.ExpiresAtUtc) has already passed, but before the
// stale-approval expiry job has swept it. Thrown by both decision paths —
// dbo.ApproveBooking under its own lock, and RejectBookingCommandRequestHandler
// as a plain pre-check — closing the poll-window race the sweep alone left
// open: a deadline that only ever took effect whenever the job next ran was
// not really a deadline.
public sealed class ApprovalRequestExpiredException : AppException
{
    public ApprovalRequestExpiredException(Guid bookingId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.ApprovalRequestExpired,
            $"Booking {bookingId}'s approval request deadline has already passed.")
    {
    }
}
