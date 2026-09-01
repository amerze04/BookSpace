using FluentValidation;

namespace BookSpace.Application.Features.Resources.CreateResource;

// Shape only, shared with the edit command. Whether the timezone id is real is
// a rule check in the handler (ReasonCodes.InvalidTimeZone).
public sealed class CreateResourceCommandRequestValidator : AbstractValidator<CreateResourceCommandRequest>
{
    public CreateResourceCommandRequestValidator()
    {
        this.AddResourceFieldRules();
    }
}
