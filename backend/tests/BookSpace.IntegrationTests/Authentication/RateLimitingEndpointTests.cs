using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace BookSpace.IntegrationTests.Authentication;

// Hardening pass, P2 security. /auth/login and /auth/refresh had no rate
// limiting at all before this pass — see Program.cs's AddRateLimiter and
// AuthController's [EnableRateLimiting] attributes. AuthenticationTestHost
// itself runs with an effectively unlimited override (every other test file
// in this suite depends on that), so proving the limiter actually rejects
// once exhausted needs its own host with a deliberately low budget —
// WithWebHostBuilder gives one that shares the same seeded database and
// signing key but overrides just the rate-limit settings.
[Collection(nameof(AuthenticationTestCollection))]
public class RateLimitingEndpointTests
{
    private readonly AuthenticationTestHost _host;

    public RateLimitingEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task Login_RejectsOnceThePermitLimitIsExhausted()
    {
        using var factory = _host.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Login:WindowSeconds", "60");
        });

        using var client = factory.CreateClient();

        // Credentials do not need to be valid: the limiter runs ahead of
        // authentication (Program.cs's pipeline ordering), so every request
        // consumes a permit regardless of outcome.
        var payload = new { email = "nobody@acme.test", password = "wrong" };

        var first = await client.PostAsJsonAsync("/auth/login", payload);
        var second = await client.PostAsJsonAsync("/auth/login", payload);
        var third = await client.PostAsJsonAsync("/auth/login", payload);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Refresh_RejectsOnceThePermitLimitIsExhausted()
    {
        using var factory = _host.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Refresh:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Refresh:WindowSeconds", "60");
        });

        using var client = factory.CreateClient();

        var payload = new { refreshToken = "not-a-real-token" };

        var first = await client.PostAsJsonAsync("/auth/refresh", payload);
        var second = await client.PostAsJsonAsync("/auth/refresh", payload);
        var third = await client.PostAsJsonAsync("/auth/refresh", payload);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // The policy is per-route: exhausting /auth/login must not throttle an
    // unrelated endpoint that carries no [EnableRateLimiting] of its own.
    [Fact]
    public async Task ExhaustingLoginsBudget_DoesNotAffectAnUnrelatedEndpoint()
    {
        using var factory = _host.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", "1");
            builder.UseSetting("RateLimiting:Login:WindowSeconds", "60");
        });

        using var client = factory.CreateClient();

        var payload = new { email = "nobody@acme.test", password = "wrong" };
        await client.PostAsJsonAsync("/auth/login", payload);
        var throttled = await client.PostAsJsonAsync("/auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        var health = await client.GetAsync("/health");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, health.StatusCode);
    }
}
