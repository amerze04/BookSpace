using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Persistence.Repositories;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<BookSpaceDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("BookSpaceDb"),
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: new[] { 1205 })));

        // ValidateOnStart so a missing or too-short signing key kills the boot
        // rather than letting the app serve forgeable tokens (CLAUDE.md §4.4).
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();

        // Scoped: these hold the request's DbContext.
        services.AddScoped<IAuthenticationUserRepository, AuthenticationUserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        return services;
    }
}
