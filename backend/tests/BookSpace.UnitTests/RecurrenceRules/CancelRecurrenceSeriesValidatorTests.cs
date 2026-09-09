using BookSpace.Application.Features.RecurrenceRules.CancelSeries;
using FluentValidation.TestHelper;

namespace BookSpace.UnitTests.RecurrenceRules;

// Shape only, mirroring CreateRecurrenceSeriesValidatorTests. Whether *this*
// series may be cancelled is a handler rule with a reason code — see
// CancelRecurrenceSeriesCommandRequestHandlerTests.
public class CancelRecurrenceSeriesValidatorTests
{
    private static readonly CancelRecurrenceSeriesCommandRequestValidator Validator = new();

    [Fact]
    public void AWellFormedRequestPasses()
    {
        Validator.TestValidate(new CancelRecurrenceSeriesCommandRequest(Guid.NewGuid(), "No longer needed"))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ANullReasonIsAccepted()
    {
        Validator.TestValidate(new CancelRecurrenceSeriesCommandRequest(Guid.NewGuid()))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void RecurrenceRuleIdIsRequired()
    {
        Validator.TestValidate(new CancelRecurrenceSeriesCommandRequest(Guid.Empty))
            .ShouldHaveValidationErrorFor(c => c.RecurrenceRuleId);
    }

    [Fact]
    public void ReasonOverTheColumnLimitIsRejected()
    {
        var request = new CancelRecurrenceSeriesCommandRequest(
            Guid.NewGuid(), new string('a', CancelRecurrenceSeriesCommandRequestValidator.MaxReasonLength + 1));

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Reason);
    }
}
