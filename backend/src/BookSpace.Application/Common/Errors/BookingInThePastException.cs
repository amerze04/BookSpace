namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-4.1). The requested interval has entirely
// elapsed, so the booking would reserve nothing.
//
// The test is on the **end** of the interval and deliberately not the start,
// which is decision 0019's BlackoutPeriodElapsed rule reapplied: a request whose
// start is in the past is the ordinary case, not an error — booking the room you
// are already sitting in, or logging the vehicle you took out an hour ago. Only
// once the whole interval is behind us does the request stop meaning anything.
//
// Refused rather than clamped, matching how an oversized pageSize is refused
// (decision 0015) rather than quietly reduced.
public sealed class BookingInThePastException : AppException
{
    public BookingInThePastException(Guid resourceId, DateTime endsAtUtc)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.BookingInThePast,
            $"The requested interval on resource {resourceId} ended at {endsAtUtc:o}, which has already passed.")
    {
    }
}
