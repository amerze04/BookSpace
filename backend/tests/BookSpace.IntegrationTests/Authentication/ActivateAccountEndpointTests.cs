using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Authentication;

// POST /auth/activate end to end, through the real pipeline — real rate-limit
// policy, real ValidationBehavior, real GlobalExceptionHandler, and above all
// real row-level security.
//
// **The last of those is why this file cannot be a unit test.** Activation is
// the first operation in this application that *writes* to a Users row from an
// unauthenticated request. With no tenant context, RLS's filter predicate hides
// that row, and an UPDATE against a hidden row affects zero rows — so the whole
// mechanism turns on IAuthenticationUserRepository.SaveChangesUnfilteredAsync
// entering the bypass. A fake repository has nothing to be hidden from and would
// pass either way; CLAUDE.md §8 says the in-memory provider has no RLS, and this
// is exactly the behaviour it would let through.
//
// Each test provisions its own throwaway user, because the host and its database
// are shared across this collection.
[Collection(nameof(AuthenticationTestCollection))]
public class ActivateAccountEndpointTests : IAsyncLifetime
{
    private const string NewPassword = "a-perfectly-good-password";

    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _provisionedUserIds = [];

    public ActivateAccountEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // **Required, not tidiness.** This is the first test class in the suite that
    // adds *users* to the shared database, and TenantIsolationTests asserts
    // Acme has exactly four of them — twice. Leaving invitees behind turns those
    // two into failures that depend on which order the collections happened to
    // run in, which is the worst kind of red. Cascade on
    // FK_ActivationTokens_Users and FK_RefreshTokens_Users takes the tokens with
    // them.
    //
    // CLAUDE.md §4.5's "nothing is deleted" governs the application, not its
    // fixtures — the same standing decision 0017 gives raw-SQL booking inserts
    // in tests.
    public async Task DisposeAsync()
    {
        if (_provisionedUserIds.Count == 0)
        {
            return;
        }

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var users = await context.Users
            .IgnoreQueryFilters()
            .Where(u => _provisionedUserIds.Contains(u.Id))
            .ToListAsync();

        context.Users.RemoveRange(users);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task AValidToken_Returns204()
    {
        var client = _host.CreateClient();
        var (_, rawToken) = await ProvisionInvitedUserAsync();

        var response = await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The point of the whole feature, and the proof the RLS bypass works: the
    // account can now be signed into with the password its owner just chose,
    // and the administrator who created it never knew one.
    [Fact]
    public async Task AfterActivating_TheUserCanLogIn()
    {
        var client = _host.CreateClient();
        var (email, rawToken) = await ProvisionInvitedUserAsync();

        (await client.PostAsJsonAsync("/auth/activate", new { token = rawToken, password = NewPassword }))
            .EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = NewPassword });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(body.GetProperty("accessToken").GetString()!);
    }

    // Before activation the account holds a placeholder hash nobody knows, so
    // there is no window in which a provisioned user is sign-innable.
    [Fact]
    public async Task BeforeActivating_TheUserCannotLogIn()
    {
        var client = _host.CreateClient();
        var (email, _) = await ProvisionInvitedUserAsync();

        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task ActivationConsumesTheToken_InTheDatabase()
    {
        var client = _host.CreateClient();
        var (_, rawToken) = await ProvisionInvitedUserAsync();

        (await client.PostAsJsonAsync("/auth/activate", new { token = rawToken, password = NewPassword }))
            .EnsureSuccessStatusCode();

        var stored = await FindTokenAsync(rawToken);
        Assert.NotNull(stored);
        Assert.True(stored!.IsConsumed);
    }

    // FR-2.3, the same property login's own test asserts for refresh tokens:
    // what is in the table must not be usable as the token.
    [Fact]
    public async Task OnlyAHashOfTheTokenIsStored()
    {
        var (_, rawToken) = await ProvisionInvitedUserAsync();

        var stored = await FindTokenAsync(rawToken);

        Assert.NotNull(stored);
        Assert.NotEqual(rawToken, stored!.TokenHash);
    }

    [Fact]
    public async Task ReusingAToken_Returns401()
    {
        var client = _host.CreateClient();
        var (_, rawToken) = await ProvisionInvitedUserAsync();

        (await client.PostAsJsonAsync("/auth/activate", new { token = rawToken, password = NewPassword }))
            .EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = "a-different-good-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    // And the password is the one the first, legitimate activation set — a
    // refused second attempt must not have half-applied.
    [Fact]
    public async Task ReusingAToken_DoesNotChangeThePassword()
    {
        var client = _host.CreateClient();
        var (email, rawToken) = await ProvisionInvitedUserAsync();

        (await client.PostAsJsonAsync("/auth/activate", new { token = rawToken, password = NewPassword }))
            .EnsureSuccessStatusCode();
        await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = "a-different-good-password" });

        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = NewPassword });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AnUnknownToken_Returns401()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/activate", new { token = "a-token-nobody-ever-issued", password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredToken_Returns401()
    {
        var client = _host.CreateClient();
        var (_, rawToken) = await ProvisionInvitedUserAsync(expiresInDays: -1);

        var response = await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // FR-2.4: the invitation outlived the decision to invite.
    [Fact]
    public async Task ATokenForADeactivatedUser_Returns401()
    {
        var client = _host.CreateClient();
        var (email, rawToken) = await ProvisionInvitedUserAsync();
        await DeactivateAsync(email);

        var response = await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // **The property the whole design exists for.** Every refusal has to be
    // byte-identical, or the endpoint tells an attacker which invitations are
    // outstanding and which accounts exist (docs/user-management-plan.md §4.2,
    // inheriting decision 0018's reasoning). Compared as whole response bodies,
    // minus the two fields that are unique per request by design — the same
    // technique the login enumeration test uses.
    [Fact]
    public async Task EveryRefusal_IsIndistinguishable()
    {
        var client = _host.CreateClient();

        var (_, expired) = await ProvisionInvitedUserAsync(expiresInDays: -1);

        var (_, consumed) = await ProvisionInvitedUserAsync();
        (await client.PostAsJsonAsync("/auth/activate", new { token = consumed, password = NewPassword }))
            .EnsureSuccessStatusCode();

        var (deactivatedEmail, deactivated) = await ProvisionInvitedUserAsync();
        await DeactivateAsync(deactivatedEmail);

        var bodies = new List<string>();
        foreach (var token in new[] { "a-token-nobody-ever-issued", expired, consumed, deactivated })
        {
            var response = await client.PostAsJsonAsync(
                "/auth/activate", new { token, password = NewPassword });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            bodies.Add(await ReadBodyWithoutRequestIdsAsync(response));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARefusal_CarriesTheReasonCode()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/activate", new { token = "a-token-nobody-ever-issued", password = NewPassword });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidActivationToken", body.GetProperty("reasonCode").GetString());
    }

    // The password policy is a 400 from ValidationBehavior, never a 401 — and
    // it is checked *before* the token is looked up, so a short password cannot
    // be used to probe whether a token is live.
    [Fact]
    public async Task APasswordShorterThanThePolicy_Returns400()
    {
        var client = _host.CreateClient();
        var (_, rawToken) = await ProvisionInvitedUserAsync();

        var response = await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AShortPasswordAnswersTheSameWayForALiveAndADeadToken()
    {
        var client = _host.CreateClient();
        var (_, live) = await ProvisionInvitedUserAsync();

        var withLive = await client.PostAsJsonAsync(
            "/auth/activate", new { token = live, password = "short" });
        var withDead = await client.PostAsJsonAsync(
            "/auth/activate", new { token = "a-token-nobody-ever-issued", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, withLive.StatusCode);
        Assert.Equal(
            await ReadBodyWithoutRequestIdsAsync(withLive),
            await ReadBodyWithoutRequestIdsAsync(withDead));
    }

    // A rejected password must leave the token spendable — otherwise a typo
    // during activation would burn the only invitation the account will ever
    // get (there is no way to re-issue one today).
    [Fact]
    public async Task ARejectedPassword_LeavesTheTokenUsable()
    {
        var client = _host.CreateClient();
        var (_, rawToken) = await ProvisionInvitedUserAsync();

        await client.PostAsJsonAsync("/auth/activate", new { token = rawToken, password = "short" });

        var retry = await client.PostAsJsonAsync(
            "/auth/activate", new { token = rawToken, password = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    // The endpoint is anonymous, but a browser that already has a session
    // attaches its bearer token to same-origin API calls — so an invitation
    // clicked while logged in as somebody else arrives with another tenant's
    // OrgId on the request. That used to be a 500 from the SaveChanges
    // ownership guard (CLAUDE.md §4.2 mechanism 2) disagreeing with the RLS
    // bypass; it has to be an ordinary activation.
    [Fact]
    public async Task ActivatingWhileSignedInAsSomebodyElse_StillWorks()
    {
        var client = _host.CreateClient();
        var (email, rawToken) = await ProvisionInvitedUserAsync();

        var otherTenant = await client.PostAsJsonAsync(
            "/auth/login", new { email = "admin@globex.test", password = SeedData.SeedPassword });
        otherTenant.EnsureSuccessStatusCode();
        var accessToken = (await otherTenant.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();

        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/activate")
        {
            Content = JsonContent.Create(new { token = rawToken, password = NewPassword }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // Creates the state phase 3's POST /users will create: a user with a
    // placeholder credential nobody knows, plus one outstanding activation
    // token. Written directly rather than through an endpoint because that
    // endpoint does not exist yet.
    private async Task<(string Email, string RawToken)> ProvisionInvitedUserAsync(int expiresInDays = 7)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var factory = scope.ServiceProvider.GetRequiredService<IActivationTokenFactory>();

        using var _ = TenantBypassScope.Enter();

        // Anchored to Acme specifically, not "the first organization": the
        // signed-in-as-somebody-else test below needs the bearer token to come
        // from the *other* tenant, and row order is not a thing to rely on.
        var acmeAdmin = await context.Users
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Email == "admin@acme.test");

        var email = $"invitee-{Guid.NewGuid():N}@acme.test";

        // Truncated to whole seconds, the CLAUDE.md §4.3 convention: datetime2(0)
        // rounds on write, so an untruncated stamp is stored as a different
        // value than the one in memory.
        var now = new DateTime(
            DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        // Derived from the expiry rather than fixed, so an "expired" token is a
        // realistic one sent eight days ago rather than one whose expiry
        // precedes its own issue — which ActivationToken refuses outright.
        var expiresAtUtc = now.AddDays(expiresInDays);
        var issuedAtUtc = expiresAtUtc.AddDays(-7);

        var user = new User(
            Guid.NewGuid(),
            acmeAdmin.OrgId,
            email,
            // A hash of something nobody knows — phase 3 will do the same. Not
            // an empty string: User's constructor refuses that, and an account
            // with no hash at all would be a different kind of broken.
            passwordHasher.Hash($"placeholder-{Guid.NewGuid():N}"),
            "Invited Person",
            acmeAdmin.Id,
            now);
        context.Users.Add(user);

        var generated = factory.Create();
        context.ActivationTokens.Add(new ActivationToken(
            Guid.NewGuid(),
            user.Id,
            generated.Hash,
            issuedAtUtc,
            expiresAtUtc));

        await context.SaveChangesAsync();

        _provisionedUserIds.Add(user.Id);
        return (email, generated.RawToken);
    }

    private async Task DeactivateAsync(string email)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var user = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email);
        user.Deactivate(user.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task<ActivationToken?> FindTokenAsync(string rawToken)
    {
        await using var scope = _host.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IActivationTokenFactory>();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var hash = factory.HashOf(rawToken);
        return await context.ActivationTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
    }

    // Strips the two properties that are unique per request by design, so what
    // is left is exactly what a caller could use to tell two failures apart.
    private static async Task<string> ReadBodyWithoutRequestIdsAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("correlationId");
        body.Remove("traceId");
        return body.ToJsonString();
    }
}
