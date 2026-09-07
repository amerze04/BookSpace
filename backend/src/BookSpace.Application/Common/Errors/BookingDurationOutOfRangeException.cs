namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-4.1, §6 tier 4). The requested length falls
// outside the resource's MinDurationMinutes / MaxDurationMinutes.
//
// The rule itself is Resource.AllowsBookingDuration, never re-derived here.
// That method was added in the 2026-09-04 corrections pass for this exact
// caller: until WP-4, MinDurationMinutes was read in one place and
// MaxDurationMinutes **nowhere at all** — stored, constrained by
// CK_Resources_DurationLimits, echoed in responses, and never enforced.
//
// Not a Validation kind, though it looks like one. The request is well-formed
// and its acceptability depends on the resource it names, which a shape
// validator cannot see — the same reasoning that makes ResourceArchived a rule
// violation rather than a bad request.
public sealed class BookingDurationOutOfRangeException : AppException
{
    public BookingDurationOutOfRangeException(Guid resourceId, int requestedMinutes)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.BookingDurationOutOfRange,
            $"A booking of {requestedMinutes} minutes is outside the duration limits of resource {resourceId}.")
    {
    }
}
