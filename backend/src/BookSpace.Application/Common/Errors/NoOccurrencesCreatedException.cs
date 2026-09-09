using BookSpace.Application.Features.RecurrenceRules;

namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-5.4, wp5-plan.md §5.1). Every occurrence in a
// recurring series request was either skipped (decision 0008's spring-forward
// gap) or refused (BookingEligibility's pre-check, or dbo.CreateBooking
// itself), so nothing was reserved — the owner's answer to WP-5's shape
// question 2 says this must not come back as a 201 with an empty list.
//
// 422 rather than 409: the ordinary case is every occurrence landing outside
// availability or inside a blackout, a rule refusal — and the rarer case,
// every occurrence losing a capacity race, is still honestly "nothing could
// be booked" rather than "conflicts with existing state".
//
// Carries the same per-occurrence breakdown a successful response would have,
// via AppException.Extensions — the second exception in this codebase (after
// FluentValidation.ValidationException) that needs to hand the client more
// than a reason code.
public sealed class NoOccurrencesCreatedException : AppException
{
    public NoOccurrencesCreatedException(IReadOnlyList<RecurrenceOccurrenceReport> occurrences)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.NoOccurrencesCreated,
            "Every occurrence in this recurring series was skipped or refused.",
            new Dictionary<string, object?> { ["occurrences"] = occurrences ?? [] })
    {
    }
}
