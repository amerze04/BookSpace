namespace BookSpace.Application.Common.Errors;

// ErrorKind.Conflict (FR-4.2, decision 0005). Units are free across the whole
// requested interval, but fewer than were asked for.
//
// The counterpart to SlotUnavailable, split on what is left rather than on the
// resource (owner's call, 2026-09-07): "you asked for three and two are free" is
// genuinely different information from "there is nothing here", and only a
// pooled resource can produce it.
//
// RemainingCapacity is nullable because the two throw sites know different
// things. dbo.CreateBooking computes the figure under its lock and passes it,
// which is the number that was true at the moment of refusal; the handler's
// pre-check answers a containment question and has no single number to report,
// so it passes null rather than inventing one.
//
// It is carried on the exception but not on the response: the message is
// log-only (decision 0016), and the reason code is what the client branches on.
public sealed class CapacityExceededException : AppException
{
    public CapacityExceededException(Guid resourceId, int? remainingCapacity)
        : base(
            ErrorKind.Conflict,
            ReasonCodes.CapacityExceeded,
            remainingCapacity is null
                ? $"Resource {resourceId} has less capacity remaining than the requested quantity."
                : $"Resource {resourceId} has only {remainingCapacity} unit(s) remaining for the requested interval.")
    {
        RemainingCapacity = remainingCapacity;
    }

    public int? RemainingCapacity { get; }
}
