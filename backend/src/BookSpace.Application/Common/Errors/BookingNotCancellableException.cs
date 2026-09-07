namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-4.4). The booking cannot be cancelled, for one of
// two reasons the client is not told apart.
//
// **Already terminal** — Cancelled, Rejected, Completed or NoShow. Booking
// .Cancel already refuses these; this is that refusal given a reason code
// instead of an InvalidOperationException. Unlike archiving a resource, a second
// cancellation is not idempotent: it would overwrite CancelledByUserId,
// CancelledAtUtc and the reason with a second actor's, so the record of who
// called the meeting off would quietly change.
//
// **Already ended** — the interval is in the past. Mirrors decision 0019's
// BlackoutPeriodElapsed exactly, and for the same reason: the test is on
// EndsAtUtc, not StartsAtUtc, so a meeting currently in progress *can* be
// cancelled while one that finished last week cannot. Cancelling something that
// already happened does not free a slot, it rewrites history — and the system
// has no other record that it took place, because nothing writes
// BookingStatus.Completed.
public sealed class BookingNotCancellableException : AppException
{
    public BookingNotCancellableException(Guid bookingId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.BookingNotCancellable,
            $"Booking {bookingId} is in a terminal status or has already ended, and cannot be cancelled.")
    {
    }
}
