using FluentValidation;

namespace BookSpace.Application.Features.Resources.GetResource;

// Guid.Empty is never a real id, so it is a malformed request (400) rather than
// a lookup that happens to miss (404). Route binding already rejects anything
// that isn't a Guid at all.
public sealed class GetResourceQueryValidator : AbstractValidator<GetResourceQuery>
{
    public GetResourceQueryValidator()
    {
        RuleFor(q => q.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");
    }
}
