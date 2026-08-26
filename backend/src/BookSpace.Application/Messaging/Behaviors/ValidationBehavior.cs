using FluentValidation;

namespace BookSpace.Application.Messaging.Behaviors;

public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_validators.Any())
        {
            return await next();
        }

        // Each validator gets its own ValidationContext — sharing one instance
        // across validators makes FluentValidation accumulate failures onto the
        // shared context, double-counting them across the returned results.
        var results = await Task.WhenAll(_validators.Select(v =>
            v.ValidateAsync(new ValidationContext<TRequest>(request), cancellationToken)));

        var failures = results.SelectMany(r => r.Errors).ToList();

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}
