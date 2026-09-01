namespace BookSpace.Application.Common.Errors;

// ErrorKind.Conflict (FR-3.2). Two windows on the same weekday whose hours
// overlap are rejected rather than unioned — docs/wp3-plan.md's "smaller
// calls". The schema constrains neither way, so this is the only thing
// enforcing it.
//
// Conflict rather than Validation, and the distinction is the whole reason this
// is not a FluentValidation rule: every window in the payload is individually
// well-formed, and each one names a field that is perfectly fine. What fails is
// the *set* contradicting itself, which no per-field error can point at.
//
// Adjacent windows are not overlapping: 09:00-12:00 followed by 12:00-17:00 is
// two legal windows, because a window's ClosesAt is exclusive. Only a strict
// interior overlap gets here.
//
// The weekday and hours are in the message for the log only — the client gets
// the code (docs/decisions/0016). Which pair collided is genuinely useful to an
// admin, and is a candidate for the per-error client-facing detail AppException
// leaves room for; not added here, because one endpoint is a poor place to
// start a convention nothing else follows yet.
public sealed class OverlappingAvailabilityWindowException : AppException
{
    public OverlappingAvailabilityWindowException(DayOfWeek weekday, TimeOnly opensAt, TimeOnly closesAt)
        : base(
            ErrorKind.Conflict,
            ReasonCodes.OverlappingAvailabilityWindow,
            $"An availability window on {weekday} at {opensAt:HH:mm}-{closesAt:HH:mm} "
            + "overlaps another window on the same weekday.")
    {
    }
}
