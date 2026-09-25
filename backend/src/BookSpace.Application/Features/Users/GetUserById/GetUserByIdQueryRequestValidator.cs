using FluentValidation;

namespace BookSpace.Application.Features.Users.GetUserById;

// Guid.Empty is never a real id, so it is a malformed request (400) rather than
// a lookup that happens to miss (404) — the same distinction
// GetResourceQueryRequestValidator draws. Route binding already rejects
// anything that isn't a Guid at all.
public sealed class GetUserByIdQueryRequestValidator : AbstractValidator<GetUserByIdQueryRequest>
{
    public GetUserByIdQueryRequestValidator()
    {
        RuleFor(q => q.UserId)
            .NotEmpty()
            .WithMessage("UserId is required.");
    }
}
