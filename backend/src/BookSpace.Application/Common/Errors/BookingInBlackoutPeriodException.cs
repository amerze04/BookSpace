namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-3.4, FR-4.3). A blackout covers part or all of the
// requested interval.
//
// Named for the booking rather than for the blackout, unlike its two neighbours
// BlackoutPeriodElapsedException and BlackoutPeriodNotFoundException: those are
// refusals to *manage* a blackout, this is a refusal to book against one. The
// reason code is ReasonCodes.BlackoutPeriod, which CLAUDE.md §6 has always
// listed as a booking rejection despite the name.
//
// Any overlap at all is enough — decision 0001 gives a blackout absolute
// priority, so a booking clipped by one minute is refused entire.
//
// Thrown from two places, deliberately. The handler's pre-check gives a booker a
// useful answer, and dbo.CreateBooking repeats the test under its lock because
// the blackout cascade can otherwise run between the two and miss a booking that
// did not exist when it looked.
public sealed class BookingInBlackoutPeriodException : AppException
{
    public BookingInBlackoutPeriodException(Guid resourceId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.BlackoutPeriod,
            $"A blackout period covers the requested interval on resource {resourceId}.")
    {
    }
}
