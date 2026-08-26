using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Authentication;

// FR-2.1 / FR-2.2 / FR-2.4 end to end over HTTP, through the real pipeline.
//
// The host (and its database) is shared across the tests in this collection, so
// every assertion is scoped to the token family under test rather than to "all
// rows" — otherwise tokens minted by a neighbouring test would break it.
[Collection(nameof(AuthenticationTestCollection))]
public class AuthenticationEndpointTests
{
    private const string SeededMember = "member1@acme.test";

    private readonly AuthenticationTestHost _host;

    public AuthenticationEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithBothTokens()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email = SeededMember, password = SeedData.SeedPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(body.GetProperty("accessToken").GetString()!);
        Assert.NotEmpty(body.GetProperty("refreshToken").GetString()!);
        Assert.Equal(900, body.GetProperty("expiresIn").GetInt32());
    }

    // Emails are stored normalized, so case shouldn't decide whether login works.
    [Fact]
    public async Task Login_EmailInDifferentCase_StillAuthenticates()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email = SeededMember.ToUpperInvariant(), password = SeedData.SeedPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // FR-2.3: the stored value must not be the token the client holds.
    [Fact]
    public async Task Login_StoresOnlyAHashOfTheRefreshToken()
    {
        var client = _host.CreateClient();

        var tokens = await LoginAsync(client, SeededMember);
        var stored = await FindTokenAsync(tokens.RefreshToken);

        Assert.NotNull(stored);
        Assert.NotEqual(tokens.RefreshToken, stored!.TokenHash);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email = SeededMember, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The two failures must be indistinguishable, or login becomes an account
    // enumeration oracle.
    [Fact]
    public async Task Login_UnknownEmail_ReturnsTheSameResponseAsAWrongPassword()
    {
        var client = _host.CreateClient();

        var unknown = await client.PostAsJsonAsync(
            "/auth/login", new { email = "nobody@acme.test", password = SeedData.SeedPassword });
        var wrongPassword = await client.PostAsJsonAsync(
            "/auth/login", new { email = SeededMember, password = "not-the-password" });

        Assert.Equal(unknown.StatusCode, wrongPassword.StatusCode);

        // The per-request identifiers necessarily differ; everything a caller
        // could learn from must not.
        Assert.Equal(
            await ReadBodyWithoutRequestIdsAsync(unknown),
            await ReadBodyWithoutRequestIdsAsync(wrongPassword));
    }

    [Fact]
    public async Task Login_Failure_CarriesAReasonCodeAndCorrelationId()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email = SeededMember, password = "not-the-password" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidCredentials", body.GetProperty("reasonCode").GetString());
        Assert.NotEmpty(body.GetProperty("correlationId").GetString()!);
    }

    // The FluentValidation behavior short-circuits before the handler runs.
    [Fact]
    public async Task Login_MalformedRequest_Returns400WithFieldErrors()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new { email = "not-an-email", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("Password", out _));
    }

    [Fact]
    public async Task Login_NeverEchoesThePasswordBack()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email = SeededMember, password = SeedData.SeedPassword });

        Assert.DoesNotContain(SeedData.SeedPassword, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_ValidToken_Returns200WithADifferentPair()
    {
        var client = _host.CreateClient();
        var original = await LoginAsync(client, SeededMember);

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = original.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(original.RefreshToken, body.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task Refresh_RotatesWithinTheSameFamilyAndLinksTheReplacement()
    {
        var client = _host.CreateClient();
        var original = await LoginAsync(client, SeededMember);

        var rotated = await RefreshAsync(client, original.RefreshToken);

        var old = (await FindTokenAsync(original.RefreshToken))!;
        var replacement = (await FindTokenAsync(rotated.RefreshToken))!;

        Assert.NotNull(old.RevokedAtUtc);
        Assert.Equal(replacement.Id, old.ReplacedByTokenId);
        Assert.Equal(old.FamilyId, replacement.FamilyId);
        Assert.Null(replacement.RevokedAtUtc);
    }

    // The window is absolute — rotating must not extend it.
    [Fact]
    public async Task Refresh_ReplacementInheritsTheOriginalExpiry()
    {
        var client = _host.CreateClient();
        var original = await LoginAsync(client, SeededMember);

        var rotated = await RefreshAsync(client, original.RefreshToken);

        var old = (await FindTokenAsync(original.RefreshToken))!;
        var replacement = (await FindTokenAsync(rotated.RefreshToken))!;

        Assert.Equal(old.ExpiresAtUtc, replacement.ExpiresAtUtc);
    }

    // FR-2.2, the property this whole design exists for.
    [Fact]
    public async Task Refresh_ReusingARotatedToken_Returns401AndRevokesTheWholeFamily()
    {
        var client = _host.CreateClient();
        var original = await LoginAsync(client, SeededMember);

        // Legitimate rotation.
        var rotated = await RefreshAsync(client, original.RefreshToken);

        // The same token presented a second time — the theft signal.
        var reuse = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = original.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        var body = await reuse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RefreshTokenReuseDetected", body.GetProperty("reasonCode").GetString());

        // Every token in that family is dead — including the one the legitimate
        // holder was still using and had done nothing wrong with.
        var familyId = (await FindTokenAsync(original.RefreshToken))!.FamilyId;
        await using (var scope = _host.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            var family = await context.RefreshTokens.Where(t => t.FamilyId == familyId).ToListAsync();

            Assert.Equal(2, family.Count);
            Assert.All(family, t => Assert.NotNull(t.RevokedAtUtc));
        }

        // And the good token really is unusable now.
        var afterFamilyRevoked = await client.PostAsJsonAsync(
            "/auth/refresh", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterFamilyRevoked.StatusCode);
    }

    // A different device's session must survive one family being compromised.
    [Fact]
    public async Task Refresh_ReuseDetected_LeavesOtherSessionsAlone()
    {
        var client = _host.CreateClient();
        var compromised = await LoginAsync(client, SeededMember);
        var otherDevice = await LoginAsync(client, SeededMember);

        await RefreshAsync(client, compromised.RefreshToken);
        await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = compromised.RefreshToken });

        var stillGood = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = otherDevice.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, stillGood.StatusCode);
    }

    [Fact]
    public async Task Refresh_UnknownToken_Returns401()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = "never-issued" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidRefreshToken", body.GetProperty("reasonCode").GetString());
    }

    // FR-2.4: deactivating a user ends their session at the next refresh.
    // Uses its own account so deactivating it can't disturb the other tests.
    [Fact]
    public async Task Refresh_UserDeactivatedSinceLogin_Returns401AndRevokesTheFamily()
    {
        var client = _host.CreateClient();
        var email = "member2@acme.test";
        var tokens = await LoginAsync(client, email);

        await using (var scope = _host.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            var user = await context.Users.FirstAsync(u => u.Email == email);
            user.Deactivate(user.Id, DateTime.UtcNow);
            await context.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AccountInactive", body.GetProperty("reasonCode").GetString());

        var familyId = (await FindTokenAsync(tokens.RefreshToken))!.FamilyId;
        await using (var scope = _host.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            var family = await context.RefreshTokens.Where(t => t.FamilyId == familyId).ToListAsync();
            Assert.All(family, t => Assert.NotNull(t.RevokedAtUtc));
        }
    }

    // And once deactivated, the account can't start a fresh session either.
    [Fact]
    public async Task Login_DeactivatedUser_Returns401()
    {
        var client = _host.CreateClient();
        var email = "approver@acme.test";

        await using (var scope = _host.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            var user = await context.Users.FirstAsync(u => u.Email == email);
            user.Deactivate(user.Id, DateTime.UtcNow);
            await context.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = SeedData.SeedPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheFamilyAndReturns204()
    {
        var client = _host.CreateClient();
        var tokens = await LoginAsync(client, SeededMember);

        var response = await client.PostAsJsonAsync("/auth/logout", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    // Silent about whether the token existed, so it can't be used to probe.
    [Fact]
    public async Task Logout_UnknownToken_StillReturns204()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/logout", new { refreshToken = "never-issued" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<TokenPair> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = SeedData.SeedPassword });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPair>())!;
    }

    private static async Task<TokenPair> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPair>())!;
    }

    // Uses the app's own factory to hash the raw token, so a test can find the
    // row belonging to a token it holds without reimplementing the hash.
    private async Task<RefreshToken?> FindTokenAsync(string rawToken)
    {
        await using var scope = _host.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IRefreshTokenFactory>();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var hash = factory.HashOf(rawToken);
        return await context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
    }

    // Strips the two properties that are unique per request by design — our
    // correlationId and the traceId AddProblemDetails stamps on — so what's
    // left is exactly what a caller could use to tell the two failures apart.
    private static async Task<string> ReadBodyWithoutRequestIdsAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("correlationId");
        body.Remove("traceId");
        return body.ToJsonString();
    }

    internal sealed record TokenPair(string AccessToken, int ExpiresIn, string RefreshToken);
}
