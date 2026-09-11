using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BookSpace.IntegrationTests.Authentication;

// Boots the real API — real middleware order, real JwtBearer handler, real
// authorization policies — against a throwaway SQL Server database, so these
// tests exercise the same pipeline production does. CLAUDE.md §8: the in-memory
// provider has no constraint enforcement, so the email-uniqueness and rotation
// behavior below would pass there while failing for real.
//
// Its own database, never the "BookSpace" dev one, following SeedDataTests.
public sealed class AuthenticationTestHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    // Supplied here rather than committed to appsettings (CLAUDE.md §4.4). Long
    // enough to satisfy the 32-character JwtOptions guard.
    private const string TestSigningKey = "integration-test-signing-key-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: the Development branch in Program.cs seeds the
        // database on startup, and these tests seed explicitly so each knows
        // exactly what exists.
        builder.UseEnvironment(Environments.Staging);

        builder.UseSetting("ConnectionStrings:BookSpaceDb", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Jwt:Issuer", "BookSpace.Api");
        builder.UseSetting("Jwt:Audience", "BookSpace.Client");
        builder.UseSetting("Jwt:AccessTokenMinutes", "15");
        builder.UseSetting("Jwt:RefreshTokenDays", "14");

        // Hardening pass, P2 security: Program.cs now rate-limits
        // /auth/login and /auth/refresh. This suite has no shared token
        // cache — most test classes log in fresh per test method — and
        // every test in a full run shares one process, so a production-sized
        // window would start rejecting logins partway through the suite
        // rather than exercising the product code these tests are actually
        // about. Effectively unlimited here, not disabled: the policy itself
        // stays wired up and reachable, which is what an accidental removal
        // would actually break.
        builder.UseSetting("RateLimiting:Login:PermitLimit", "1000000");
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "60");
        builder.UseSetting("RateLimiting:Refresh:PermitLimit", "1000000");
        builder.UseSetting("RateLimiting:Refresh:WindowSeconds", "60");

        // Makes PolicyProbeController discoverable. The API has no business
        // endpoints yet, so without it there is nothing for the authorization
        // policies to guard in a test.
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(PolicyProbeController).Assembly));
    }

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        // WP-4 Phase 3: the seed writes Bookings now, through dbo.CreateBooking
        // like every other write path (CLAUDE.md §4.1).
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var timeZones = scope.ServiceProvider.GetRequiredService<ITimeZoneCatalog>();

        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
        await SeedData.SeedAsync(context, passwordHasher, bookings, timeZones);
    }

    public new async Task DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            await context.Database.EnsureDeletedAsync();
        }

        await base.DisposeAsync();
    }

    // A fresh DbContext outside the request pipeline, for tests that need to
    // inspect or tamper with rows directly.
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();
}
