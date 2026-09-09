using FluentValidation;

namespace BookSpace.Application.Features.RecurrenceRules.CancelSeries;

// Shape only, mirroring CancelBookingCommandRequestValidator. Whether *this*
// series may be cancelled needs the series and the clock, so it is a rule
// check in the handler carrying RecurrenceRuleNotCancellable.
public sealed class CancelRecurrenceSeriesCommandRequestValidator
    : AbstractValidator<CancelRecurrenceSeriesCommandRequest>
{
    // Matches Bookings.CancellationReason NVARCHAR(300) — every cancelled
    // occurrence gets this same reason text, so it is validated once here
    // rather than once per occurrence.
    public const int MaxReasonLength = 300;

    public CancelRecurrenceSeriesCommandRequestValidator()
    {
        RuleFor(c => c.RecurrenceRuleId)
            .NotEmpty()
            .WithMessage("RecurrenceRuleId is required.");

        RuleFor(c => c.Reason)
            .MaximumLength(MaxReasonLength)
            .When(c => c.Reason is not null);
    }
}
