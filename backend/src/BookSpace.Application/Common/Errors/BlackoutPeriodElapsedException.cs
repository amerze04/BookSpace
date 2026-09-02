namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-3.4). A blackout whose whole interval is already
// over — EndsAtUtc at or before now.
//
// Refused because such a blackout cannot do the one thing a blackout is for:
// nothing can be booked into a window that has passed, so it blocks nothing.
// Its only reachable effect would be the decision 0001 cascade reaching
// backwards into bookings that already happened, and Booking
// .CanBeCancelledForBlackout refuses those — leaving an admin with a row that
// looks like an action and had none. Owner's call, 2026-09-02.
//
// Deliberately NOT a check on StartsAtUtc. A blackout that began in the past
// and runs into the future is the ordinary "the room flooded this morning and
// is unusable until Friday" case, and a StartsAtUtc >= now rule would also
// reject a request assembled a few seconds ago over nothing but clock skew.
//
// RuleViolation, not Validation: EndsAtUtc > StartsAtUtc holds, every field is
// well-formed, and the payload is internally consistent. What refuses it is
// what the interval *means* relative to now — the same footing as
// ResourceArchived, and unlike InvalidTimeZone, where the value itself is wrong.
public sealed class BlackoutPeriodElapsedException : AppException
{
    public BlackoutPeriodElapsedException(DateTime endsAtUtc, DateTime nowUtc)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.BlackoutPeriodElapsed,
            $"A blackout ending {endsAtUtc:o} has already elapsed at {nowUtc:o} and would block nothing.")
    {
    }
}
