using FluentValidation;

namespace BookSpace.Application.Features.Resources.ArchiveResource;

// Guid.Empty is a malformed request, not a lookup that misses — same reasoning
// as GetResourceQueryRequestValidator and UpdateResourceCommandRequestValidator.
public sealed class ArchiveResourceCommandRequestValidator : AbstractValidator<ArchiveResourceCommandRequest>
{
    public ArchiveResourceCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");
    }
}
