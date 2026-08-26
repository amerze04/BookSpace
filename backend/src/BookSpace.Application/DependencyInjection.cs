using BookSpace.Application.Messaging;
using BookSpace.Application.Messaging.Behaviors;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            foreach (var implementedInterface in type.GetInterfaces())
            {
                if (!implementedInterface.IsGenericType)
                {
                    continue;
                }

                var definition = implementedInterface.GetGenericTypeDefinition();
                if (definition == typeof(IRequestHandler<,>) || definition == typeof(IValidator<>))
                {
                    services.AddScoped(implementedInterface, type);
                }
            }
        }

        // Logging is registered first so it ends up outermost (see Dispatcher);
        // Validation runs next, before the handler ever sees the request.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<ISender, Dispatcher>();

        return services;
    }
}
