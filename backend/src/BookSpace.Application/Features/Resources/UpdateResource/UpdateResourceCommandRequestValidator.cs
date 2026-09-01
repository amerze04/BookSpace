using FluentValidation;

namespace BookSpace.Application.Features.Resources.UpdateResource;

public sealed class UpdateResourceCommandRequestValidator : AbstractValidator<UpdateResourceCommandRequest>
{
    public UpdateResourceCommandRequestValidator()
    {
        // Guid.Empty is a malformed request, not a lookup that misses — same
        // reasoning as GetResourceQueryRequestValidator.
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        this.AddResourceFieldRules();
    }
}
