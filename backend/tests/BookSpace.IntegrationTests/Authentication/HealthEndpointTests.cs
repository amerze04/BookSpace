using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.IntegrationTests.Authentication;

namespace BookSpace.IntegrationTests;

// Hardening pass, P2 security. /health/db is [AllowAnonymous] by design — a
// load balancer or uptime monitor has no credentials — so its contract must
// stay a bare status, never the database name, the SQL Server host, or a raw
// SqlException.Message. No such test existed before this pass.
[Collection(nameof(AuthenticationTestCollection))]
public class HealthEndpointTests
{
    private readonly AuthenticationTestHost _host;

    public HealthEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task GetDb_RequiresNoAuthentication()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/health/db");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDb_OnSuccess_ReportsOnlyStatus()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/health/db");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("healthy", body.GetProperty("status").GetString());

        // The leak this closes: neither of these fields may exist at all,
        // regardless of what they'd contain.
        Assert.False(body.TryGetProperty("database", out _));
        Assert.False(body.TryGetProperty("server", out _));
        Assert.False(body.TryGetProperty("error", out _));

        // Exactly one field — the contract is deliberately minimal, not just
        // missing the two specific fields named above.
        var propertyCount = 0;
        foreach (var _ in body.EnumerateObject())
        {
            propertyCount++;
        }

        Assert.Equal(1, propertyCount);
    }

    [Fact]
    public async Task Get_TopLevelHealthCheck_StillJustReportsStatus()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }
}
