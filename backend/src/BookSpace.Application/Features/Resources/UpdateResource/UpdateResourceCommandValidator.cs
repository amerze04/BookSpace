using FluentValidation;

namespace BookSpace.Application.Features.Resources.UpdateResource;

public sealed class UpdateResourceCommandValidator : AbstractValidator<UpdateResourceCommand>
{
    public UpdateResourceCommandValidator()
    {
        // Guid.Empty is a malformed request, not a lookup that misses — same
        // reasoning as GetResourceQueryValidator.
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        this.AddResourceFieldRules();
    }
}
