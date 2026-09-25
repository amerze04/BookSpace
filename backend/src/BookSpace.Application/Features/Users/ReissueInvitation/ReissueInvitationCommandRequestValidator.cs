using FluentValidation;

namespace BookSpace.Application.Features.Users.ReissueInvitation;

// Guid.Empty is never a real id — a malformed request (400), not a lookup that
// happens to miss (404) — matching GetUserByIdQueryRequestValidator.
public sealed class ReissueInvitationCommandRequestValidator : AbstractValidator<ReissueInvitationCommandRequest>
{
    public ReissueInvitationCommandRequestValidator()
    {
        RuleFor(c => c.UserId)
            .NotEmpty()
            .WithMessage("UserId is required.");
    }
}
