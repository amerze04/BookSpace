namespace BookSpace.Application.Common.Errors;

// ErrorKind.Conflict (FR-4.2, AC-1). No units are free at some instant inside
// the requested interval.
//
// **This is the rejection AC-1 is about**: given one remaining slot and two
// simultaneous requests, exactly one succeeds and the other gets this. The
// authoritative throw is the one that follows dbo.CreateBooking's answer, which
// is decided under a UPDLOCK/HOLDLOCK range lock — the handler's pre-check can
// only report what was true a moment earlier
// (docs/decisions/0023-booking-concurrency-strategy.md).
//
// Conflict, not RuleViolation, and the distinction is real: nothing about the
// request is wrong. It was refused because of what else exists, and it may well
// succeed if retried after a cancellation.
//
// Distinct from CapacityExceeded by what is left rather than by the resource:
// this one means nothing at all is free. On an exclusive resource it is the only
// capacity refusal possible, since Capacity 1 admits no quantity but 1.
public sealed class SlotUnavailableException : AppException
{
    public SlotUnavailableException(Guid resourceId)
        : base(
            ErrorKind.Conflict,
            ReasonCodes.SlotUnavailable,
            $"Resource {resourceId} has no capacity remaining for the requested interval.")
    {
    }
}
