using FluentValidation;

namespace BookSpace.Application.Features.RecurrenceRules.CreateSeries;

// Shape only. Everything that needs the resource, the clock, the calendar or
// the resource's own timezone is a rule check in the handler carrying a
// reason code — duration, availability, blackouts and capacity all depend on
// state this validator cannot see, exactly as CreateBookingCommandRequestValidator's
// header explains for the single-booking path.
public sealed class CreateRecurrenceSeriesCommandRequestValidator
    : AbstractValidator<CreateRecurrenceSeriesCommandRequest>
{
    // Matches Bookings.Title NVARCHAR(200) — every occurrence shares this
    // title (wp5-plan.md §5.1, smaller call 2), so it is validated once here
    // rather than once per occurrence.
    public const int MaxTitleLength = 200;

    public CreateRecurrenceSeriesCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // CK_RecurrenceRules_Interval, restated as a per-field message so it
        // is a 400 rather than a 500 from the RecurrenceRule constructor's
        // ArgumentOutOfRangeException.
        RuleFor(c => c.IntervalValue)
            .GreaterThan(0)
            .WithMessage("IntervalValue must be greater than zero.");

        // Not backed by a DB constraint (RecurrenceRule.cs explains why) but
        // restated here for the same reason as the interval: a 400 naming the
        // field, not a 500 from the constructor's guard.
        RuleFor(c => c.LocalEndTime)
            .GreaterThan(c => c.LocalStartTime)
            .WithMessage("LocalEndTime must be after LocalStartTime.");

        // CK_RecurrenceRules_EndCondition: exactly one of EndDate or
        // OccurrenceCount.
        RuleFor(c => c)
            .Must(c => (c.EndDate is null) != (c.OccurrenceCount is null))
            .WithMessage("Exactly one of EndDate or OccurrenceCount must be set.")
            .OverridePropertyName("EndDate");

        RuleFor(c => c.OccurrenceCount)
            .GreaterThan(0)
            .When(c => c.OccurrenceCount is not null)
            .WithMessage("OccurrenceCount must be greater than zero.");

        RuleFor(c => c.EndDate)
            .GreaterThanOrEqualTo(c => c.StartDate)
            .When(c => c.EndDate is not null)
            .WithMessage("EndDate must not be before StartDate.");

        // CK_Bookings_Quantity, restated exactly as CreateBookingCommandRequestValidator
        // does — no upper bound here either, for the same reason: what is too
        // many depends on the resource's Capacity, which this cannot see.
        RuleFor(c => c.Quantity)
            .GreaterThan(0)
            .WithMessage("Quantity must be greater than zero.");

        RuleFor(c => c.Title)
            .MaximumLength(MaxTitleLength)
            .When(c => c.Title is not null);
    }
}
