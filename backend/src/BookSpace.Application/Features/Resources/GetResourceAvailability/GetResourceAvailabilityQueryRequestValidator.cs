using FluentValidation;

namespace BookSpace.Application.Features.Resources.GetResourceAvailability;

// Request shape only. Typed against the concrete query type, per CLAUDE.md §12's
// discovery gotcha: IValidator<T> is invariant, so a validator written against a
// base or an interface would never be resolved for this one.
public sealed class GetResourceAvailabilityQueryRequestValidator
    : AbstractValidator<GetResourceAvailabilityQueryRequest>
{
    public GetResourceAvailabilityQueryRequestValidator()
    {
        RuleFor(q => q.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // CK_Bookings_Quantity: a booking holds at least one unit, so asking what
        // is free for zero of them is meaningless rather than empty.
        //
        // Deliberately not capped here. The ceiling is the resource's Capacity,
        // which a shape validator cannot see without loading the resource, and
        // asking for more units than exist is answered with an empty list — a
        // true answer, not a malformed request.
        RuleFor(q => q.Quantity)
            .GreaterThan(0)
            .WithMessage("Quantity must be greater than zero.");

        // Both dates are required and the query type declares them non-nullable,
        // so an omitted query parameter binds to default(DateOnly) — 0001-01-01 —
        // rather than failing to bind. Checked explicitly, because the alternative
        // is answering a question about the first year of the calendar and
        // returning an empty list, which reads as "nothing is bookable".
        RuleFor(q => q.FromLocalDate)
            .Must(date => date != default)
            .WithMessage("FromLocalDate is required.");

        RuleFor(q => q.ToLocalDate)
            .Must(date => date != default)
            .WithMessage("ToLocalDate is required.");

        // Equal is legal: a single day is the PRD's own flow ("selects a resource
        // and date"). Only an inverted range is refused — it would otherwise
        // return an empty list, which a client cannot tell from a closed resource.
        RuleFor(q => q.ToLocalDate)
            .GreaterThanOrEqualTo(q => q.FromLocalDate)
            .When(q => q.FromLocalDate != default && q.ToLocalDate != default)
            .WithMessage("ToLocalDate must not be earlier than FromLocalDate.");

        // Rejected, not clamped — see AvailabilityQueryRules for why, and for why
        // this is a ValidationFailed 400 rather than a new reason code.
        RuleFor(q => q.ToLocalDate)
            .Must((query, toLocalDate) =>
                AvailabilityQueryRules.RangeLengthInDays(query.FromLocalDate, toLocalDate)
                    <= AvailabilityQueryRules.MaxRangeDays)
            .When(q => q.FromLocalDate != default
                && q.ToLocalDate != default
                && q.ToLocalDate >= q.FromLocalDate)
            .WithMessage($"The range must not exceed {AvailabilityQueryRules.MaxRangeDays} days.");
    }
}
