using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Persistence.Repositories;
using BookSpace.Infrastructure.Security;
using BookSpace.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Scoped so it resolves the request's own ICurrentTenant — CLAUDE.md
        // §4.2 mechanism 3 (RLS via sp_set_session_context).
        services.AddScoped<TenantSessionContextInterceptor>();

        // (serviceProvider, options) overload, not the plain lambda: the
        // interceptor needs to be resolved from the same scope the DbContext
        // itself is being constructed in, not the root provider.
        services.AddDbContext<BookSpaceDbContext>((serviceProvider, options) =>
            options
                .UseSqlServer(
                    configuration.GetConnectionString("BookSpaceDb"),
                    sql => sql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: new[] { 1205 }))
                .AddInterceptors(serviceProvider.GetRequiredService<TenantSessionContextInterceptor>()));

        // ValidateOnStart so a missing or too-short signing key kills the boot
        // rather than letting the app serve forgeable tokens (CLAUDE.md §4.4).
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        // Stateless; TimeZoneInfo does its own caching.
        services.AddSingleton<ITimeZoneCatalog, SystemTimeZoneCatalog>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();

        // Scoped: these hold the request's DbContext.
        services.AddScoped<IAuthenticationUserRepository, AuthenticationUserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IResourceRepository, ResourceRepository>();

        return services;
    }
}
