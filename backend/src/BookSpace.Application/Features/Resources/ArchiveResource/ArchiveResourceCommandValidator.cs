using FluentValidation;

namespace BookSpace.Application.Features.Resources.ArchiveResource;

// Guid.Empty is a malformed request, not a lookup that misses — same reasoning
// as GetResourceQueryValidator and UpdateResourceCommandValidator.
public sealed class ArchiveResourceCommandValidator : AbstractValidator<ArchiveResourceCommand>
{
    public ArchiveResourceCommandValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");
    }
}
