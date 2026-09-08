namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-4.3). The requested interval is not wholly inside
// the resource's availability windows.
//
// **Wholly**, not partly: a booking running past closing time is refused, never
// truncated to fit. Nothing in this system silently changes what a caller asked
// for — the same reasoning that rejects an oversized pageSize instead of
// clamping it (decision 0015).
//
// Reported ahead of a blackout or a capacity refusal when more than one applies.
// The order is fixed in BookingEligibility, so a client's handling of a given
// request never changes for reasons it cannot see, and the most structural
// answer wins: "the resource is never open then" stays true tomorrow, while
// "someone else has it" does not.
public sealed class OutsideAvailabilityException : AppException
{
    public OutsideAvailabilityException(Guid resourceId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.OutsideAvailability,
            $"The requested interval is outside the availability windows of resource {resourceId}.")
    {
    }
}
