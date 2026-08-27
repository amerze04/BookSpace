using System.Text;
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

    builder.Services.AddControllers();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

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
        await SeedData.SeedAsync(db, passwordHasher);
    }

    app.UseHttpsRedirection();

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
