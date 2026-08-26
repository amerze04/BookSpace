using FluentValidation;

namespace BookSpace.Application.Features.Ping;

public sealed class PingCommandValidator : AbstractValidator<PingCommand>
{
    public PingCommandValidator()
    {
        RuleFor(x => x.Message).NotEmpty();
    }
}
