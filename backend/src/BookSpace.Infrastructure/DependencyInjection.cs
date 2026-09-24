using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Email;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Persistence.Repositories;
using BookSpace.Infrastructure.Security;
using BookSpace.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // Same ValidateOnStart posture as JwtOptions: a deployment that cannot
        // send email fails the boot rather than dropping invitations silently.
        // The rules live in EmailOptionsValidator rather than in annotations
        // because half of them depend on DeliveryMode — see that file.
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        // Read once, here, rather than resolved per send behind a factory: which
        // sender is in play is a deployment decision, and having it show up in
        // the DI graph means "why did no email arrive?" is answered by looking
        // at one registration. Changing it needs a restart, like every other
        // wiring decision in this file.
        //
        // An unparseable value throws here and kills the boot, which is the
        // intended direction — there is no silent fallback to either mode.
        var deliveryMode = configuration
            .GetSection(EmailOptions.SectionName)
            .GetValue(nameof(EmailOptions.DeliveryMode), EmailDeliveryMode.Smtp);

        if (deliveryMode is EmailDeliveryMode.DevelopmentSink)
        {
            services.AddSingleton<IEmailSender, DevelopmentSinkEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }

        // Nothing secret in this section, so unlike Jwt and Email it is
        // committed whole — but it is still ValidateOnStart, so a nonsensical
        // invitation lifetime fails the boot rather than issuing tokens that
        // expire before they arrive.
        services.AddOptions<ActivationOptions>()
            .Bind(configuration.GetSection(ActivationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        // Stateless; TimeZoneInfo does its own caching.
        services.AddSingleton<ITimeZoneCatalog, SystemTimeZoneCatalog>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();
        services.AddSingleton<IActivationTokenFactory, ActivationTokenFactory>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();

        // Scoped: these hold the request's DbContext.
        services.AddScoped<IAuthenticationUserRepository, AuthenticationUserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IActivationTokenRepository, ActivationTokenRepository>();
        services.AddScoped<IResourceRepository, ResourceRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBlackoutPeriodRepository, BlackoutPeriodRepository>();
        services.AddScoped<IAvailabilityRepository, AvailabilityRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IRecurrenceRuleRepository, RecurrenceRuleRepository>();

        // Scoped, like the repositories, and for the same reason: it wraps the
        // request's own DbContext and its transaction (CLAUDE.md §5).
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
