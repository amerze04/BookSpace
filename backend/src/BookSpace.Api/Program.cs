using System.Text;
using System.Text.Json.Serialization;
using BookSpace.Api.Authorization;
using BookSpace.Api.ExceptionHandling;
using BookSpace.Api.Middleware;
using BookSpace.Api.Tenancy;
using BookSpace.Application;
using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting BookSpace.Api");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Add services to the container.

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApplication();

    // CLAUDE.md §4.2: ICurrentTenant reads HttpContext.User, so it (and the
    // accessor it depends on) lives here rather than in AddInfrastructure —
    // BookSpace.Infrastructure has no ASP.NET Core dependency and shouldn't
    // gain one just for this. Scoped: it reads the current request's claims.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentTenant, HttpContextCurrentTenant>();
    // Same reasoning, for the `sub` claim the write paths audit against.
    builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

    // FR-2.1. Validation parameters per
    // docs/decisions/0009-jwt-claims-and-token-lifetimes.md. JwtOptions itself is
    // bound and validated in AddInfrastructure, so a missing signing key has
    // already failed the boot by the time this runs.
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                ?? throw new InvalidOperationException($"Missing '{JwtOptions.SectionName}' configuration section.");

            // Keep 'sub' as 'sub' instead of silently remapping it to the long
            // ClaimTypes.NameIdentifier URI, so what we read back matches what
            // JwtAccessTokenService issued.
            options.MapInboundClaims = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                // Default is 5 minutes, which would make a 15-minute token live
                // 20. "Short-lived" should mean what it says.
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                NameClaimType = "sub",
            };
        });

    builder.Services.AddAuthorization(options => options.AddBookSpacePolicies());

    // Frontend origin(s) only — never AllowAnyOrigin. The access token travels
    // as an Authorization header and the refresh token in the request body
    // (no cookies anywhere in this API), so the policy needs no
    // AllowCredentials(). "Cors:AllowedOrigins" is empty by default
    // (appsettings.json) and set to the Angular dev server in
    // appsettings.Development.json.
    const string FrontendCorsPolicy = "Frontend";
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? [];
    builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

    // Enums on the wire as their names, not their ordinals (WP-3 Phase 3).
    //
    // Forced by DayOfWeek, the first enum this API ever serializes: an
    // availability window sent as {"weekday": 1} is unreadable, and 0 = Sunday
    // is the classic off-by-one a client discovers in production. "Monday" is
    // self-describing and cannot be misread.
    //
    // Applied globally rather than to this one property, because the codebase
    // already made this choice everywhere else: CLAUDE.md §5 stores enums with
    // HasConversion<string>() and a CHECK constraint, never as int, so the wire
    // now agrees with the database and with the PRD's own status names. WP-4's
    // BookingStatus arriving as "Confirmed" rather than 1 is the payoff.
    //
    // Safe to make global *now* specifically because nothing else serializes an
    // enum yet — every existing response is primitives and strings — so this
    // breaks no contract. It would have been a breaking change a phase later.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services
        .AddControllers()
        .AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    // Hardening pass, P2 security. /auth/login and /auth/refresh had no rate
    // limiting at all — a credential-stuffing or refresh-token-guessing loop
    // could run unthrottled. Fixed-window, partitioned by remote IP: simple,
    // matches "N attempts per window" intuitively, and IP is what this API
    // can trust without assuming anything about a reverse proxy's own
    // X-Forwarded-For handling — a spoofable header is not used as the
    // partition key. Defaults are conservative starting points, not
    // load-tested production numbers (see appsettings.json's own comment);
    // both are overridden to an effectively unlimited value in
    // AuthenticationTestHost, since the integration suite logs in far more
    // often, and with no shared token cache, than any real client would in
    // the same window.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("login", httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:Login:PermitLimit", 10),
                Window = TimeSpan.FromSeconds(
                    builder.Configuration.GetValue("RateLimiting:Login:WindowSeconds", 60)),
                QueueLimit = 0,
            }));

        options.AddPolicy("refresh", httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:Refresh:PermitLimit", 20),
                Window = TimeSpan.FromSeconds(
                    builder.Configuration.GetValue("RateLimiting:Refresh:WindowSeconds", 60)),
                QueueLimit = 0,
            }));
    });

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
    });

    var app = builder.Build();

    app.UseCorrelationId();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        // The seed writes Bookings as of WP-4 Phase 3, and CLAUDE.md §4.1 leaves
        // one way to do that: dbo.CreateBooking, behind IBookingRepository. The
        // timezone catalog comes with it because the seeded intervals are
        // resource-local wall clocks (decision 0003).
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var timeZones = scope.ServiceProvider.GetRequiredService<ITimeZoneCatalog>();

        await SeedData.SeedAsync(db, passwordHasher, bookings, timeZones);
    }

    app.UseHttpsRedirection();

    // Must run before authentication/authorization: a preflight OPTIONS
    // request carries no Authorization header, so CORS has to be resolved
    // first or the browser's preflight never gets past auth to see it.
    app.UseCors(FrontendCorsPolicy);

    // Before authentication, deliberately: a request over the login/refresh
    // budget is rejected before this API spends any work validating a token
    // or hashing a password against it.
    app.UseRateLimiter();

    // Authentication must run before authorization — it's what puts the
    // principal on the context that the policies then evaluate.
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "BookSpace.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program
{
}
