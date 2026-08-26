using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.Application.Messaging;

internal sealed class Dispatcher : ISender
{
    private static readonly MethodInfo InvokeCoreDefinition = typeof(Dispatcher)
        .GetMethod(nameof(InvokeCore), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly IServiceProvider _serviceProvider;

    public Dispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        // The request's concrete type is only known at runtime, so one reflective
        // call bridges into InvokeCore, where TRequest is a real generic parameter
        // again and everything past this point is ordinary statically-typed code.
        var invoker = InvokeCoreDefinition.MakeGenericMethod(request.GetType(), typeof(TResponse));
        return (Task<TResponse>)invoker.Invoke(this, new object?[] { request, cancellationToken })!;
    }

    private async Task<TResponse> InvokeCore<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var handler = _serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

        RequestHandlerDelegate<TResponse> pipeline = () => handler.Handle(request, cancellationToken);

        // Registered order is [Logging, Validation]; wrapping in reverse order
        // means Validation is wrapped first (innermost, closest to the handler)
        // and Logging is wrapped last, ending up as the outermost link.
        var behaviors = _serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse();
        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.Handle(request, next, cancellationToken);
        }

        return await pipeline();
    }
}
