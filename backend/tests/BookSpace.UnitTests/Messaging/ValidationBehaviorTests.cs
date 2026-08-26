using BookSpace.Application.Messaging;
using BookSpace.Application.Messaging.Behaviors;
using FluentValidation;

namespace BookSpace.UnitTests.Messaging;

public class ValidationBehaviorTests
{
    [Fact]
    public async Task Handle_NoValidatorsRegistered_CallsNextUnchanged()
    {
        var behavior = new ValidationBehavior<PingRequest, string>(Array.Empty<IValidator<PingRequest>>());

        var result = await behavior.Handle(new PingRequest(""), () => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Handle_ValidatorPasses_CallsNext()
    {
        var behavior = new ValidationBehavior<PingRequest, string>(new[] { new PassingValidator() });

        var result = await behavior.Handle(new PingRequest("hello"), () => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Handle_ValidatorFails_ThrowsAndNeverCallsNext()
    {
        var behavior = new ValidationBehavior<PingRequest, string>(new[] { new FailingValidator("must not be empty") });
        var nextCalled = false;

        var exception = await Assert.ThrowsAsync<ValidationException>(() => behavior.Handle(
            new PingRequest(""),
            () =>
            {
                nextCalled = true;
                return Task.FromResult("ok");
            },
            CancellationToken.None));

        Assert.False(nextCalled);
        Assert.Contains(exception.Errors, e => e.PropertyName == "Message" && e.ErrorMessage == "must not be empty");
    }

    [Fact]
    public async Task Handle_MultipleValidatorsFail_AggregatesAllFailures()
    {
        var behavior = new ValidationBehavior<PingRequest, string>(new IValidator<PingRequest>[]
        {
            new FailingValidator("must not be empty"),
            new FailingValidator("must be at least 3 characters"),
        });

        var exception = await Assert.ThrowsAsync<ValidationException>(() => behavior.Handle(
            new PingRequest(""),
            () => Task.FromResult("ok"),
            CancellationToken.None));

        Assert.Equal(2, exception.Errors.Count());
    }

    private sealed record PingRequest(string Message) : IRequest<string>;

    private sealed class PassingValidator : AbstractValidator<PingRequest>
    {
    }

    private sealed class FailingValidator : AbstractValidator<PingRequest>
    {
        public FailingValidator(string errorMessage)
        {
            RuleFor(x => x.Message).Must(_ => false).WithMessage(errorMessage);
        }
    }
}
