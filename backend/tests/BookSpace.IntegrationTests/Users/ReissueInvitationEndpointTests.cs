using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Email;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BookSpace.IntegrationTests.Users;

// `POST /users/{id}/invitation` — hardening pass, 2026-09-25 (finding 3). The
// recovery path an expired, lost or never-delivered invitation did not have,
// through the real pipeline: real policy stack (including
// AuthorizationPolicies.ActiveTenantAdminWrite, finding 1), real
// ActivationTokens table with its SupersededAtUtc concurrency token, and the
// real development email sink.
[Collection(nameof(AuthenticationTestCollection))]
public class ReissueInvitationEndpointTests : IAsyncLifetime
{
    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";

    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _createdUserIds = [];

    public ReissueInvitationEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_createdUserIds.Count == 0)
        {
            return;
        }

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var users = await context.Users
            .IgnoreQueryFilters()
            .Where(u => _createdUserIds.Contains(u.Id))
            .ToListAsync();

        context.Users.RemoveRange(users);
        await context.SaveChangesAsync();
    }

    // ---- The happy path ----

    [Fact]
    public async Task ItAnswers200AndReportsTheInvitationWasSent()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, _) = await CreateUnactivatedUserAsync(admin);

        var response = await admin.PostAsync($"/users/{id}/invitation", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.True(body.GetProperty("invitationEmailSent").GetBoolean());
    }

    [Fact]
    public async Task TheResponseNeverCarriesARawActivationLink()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, _) = await CreateUnactivatedUserAsync(admin);

        var response = await admin.PostAsync($"/users/{id}/invitation", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.TryGetProperty("activationLink", out _));
        Assert.True(body.TryGetProperty("activationLinkExpiresAtUtc", out _));
    }

    // The whole point: a reissued invitation actually works, end to end,
    // reading the token from the *new* message.
    [Fact]
    public async Task TheNewLinkActivatesTheAccount()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, email) = await CreateUnactivatedUserAsync(admin);

        (await admin.PostAsync($"/users/{id}/invitation", null)).EnsureSuccessStatusCode();

        var message = await FindLatestSentMessageToAsync(email);
        var token = TokenFrom(message);

        var activate = await _host.CreateClient().PostAsJsonAsync(
            "/auth/activate", new { token, password = "a-perfectly-good-password" });
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
    }

    // ---- Superseding ----

    [Fact]
    public async Task TheOriginalLinkNoLongerActivatesAfterAReissue()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, email) = await CreateUnactivatedUserAsync(admin);
        var originalToken = TokenFrom(await FindLatestSentMessageToAsync(email));

        (await admin.PostAsync($"/users/{id}/invitation", null)).EnsureSuccessStatusCode();

        var activate = await _host.CreateClient().PostAsJsonAsync(
            "/auth/activate", new { token = originalToken, password = "a-perfectly-good-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, activate.StatusCode);
    }

    // Calling this repeatedly must never leave two simultaneously usable
    // credentials outstanding.
    [Fact]
    public async Task RepeatedReissuesLeaveExactlyOneRedeemableToken()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, _) = await CreateUnactivatedUserAsync(admin);

        (await admin.PostAsync($"/users/{id}/invitation", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/users/{id}/invitation", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/users/{id}/invitation", null)).EnsureSuccessStatusCode();

        var live = await FindTokensForAsync(id);
        Assert.Single(live, t => !t.IsConsumed && !t.IsSuperseded && t.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(4, live.Count); // the original from POST /users, plus three reissues.
    }

    // ---- Refusals ----

    [Fact]
    public async Task AnAlreadyActivatedAccountIsRefused()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, email) = await CreateUnactivatedUserAsync(admin);
        var token = TokenFrom(await FindLatestSentMessageToAsync(email));
        (await _host.CreateClient().PostAsJsonAsync(
                "/auth/activate", new { token, password = "a-perfectly-good-password" }))
            .EnsureSuccessStatusCode();

        var response = await admin.PostAsync($"/users/{id}/invitation", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UserAlreadyActivated", problem.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task ADeactivatedAccountIsRefused()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, _) = await CreateUnactivatedUserAsync(admin);
        (await admin.PostAsync($"/users/{id}/deactivate", null)).EnsureSuccessStatusCode();

        var response = await admin.PostAsync($"/users/{id}/invitation", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UserNotActive", problem.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task AnUnknownIdIsNotFound()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await admin.PostAsync($"/users/{Guid.NewGuid()}/invitation", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // AC-4: a real id in another tenant answers exactly like an unknown one.
    [Fact]
    public async Task AnotherTenantsRealIdIsNotFound()
    {
        var globexAdmin = await AuthenticatedClientAsync(GlobexAdmin);
        var globexAdminId = (await FindUserByEmailAsync(GlobexAdmin)).Id;

        var acmeAdmin = await AuthenticatedClientAsync(AcmeAdmin);
        var response = await acmeAdmin.PostAsync($"/users/{globexAdminId}/invitation", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        GC.KeepAlive(globexAdmin);
    }

    // ---- Authorization ----

    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task ANonAdminIsForbidden(string email)
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, _) = await CreateUnactivatedUserAsync(admin);

        var client = await AuthenticatedClientAsync(email);
        var response = await client.PostAsync($"/users/{id}/invitation", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCallerIs401()
    {
        var response = await _host.CreateClient().PostAsync($"/users/{Guid.NewGuid()}/invitation", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Concurrency ----

    // Two admins (or one admin double-clicking) both trying to resend at once.
    // Exactly one may succeed in the sense of "the token it issued is the one
    // that survives" — SupersededAtUtc's concurrency token is what decides it,
    // the same shape ConsumedAtUtc already gives two simultaneous activations.
    [Fact]
    public async Task TwoSimultaneousReissuesLeaveExactlyOneRedeemableToken()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, _) = await CreateUnactivatedUserAsync(admin);

        var first = admin.PostAsync($"/users/{id}/invitation", null);
        var second = admin.PostAsync($"/users/{id}/invitation", null);
        var responses = await Task.WhenAll(first, second);

        // Both may report 200 (the loser can retry transparently inside
        // EnableRetryOnFailure, or simply not collide if the two requests do
        // not truly interleave — the same caveat this suite's other
        // TestServer-timing concurrency tests carry) — what must never happen
        // is the account ending up with two simultaneously live tokens.
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"Unexpected status {r.StatusCode}"));

        var live = (await FindTokensForAsync(id))
            .Where(t => !t.IsConsumed && !t.IsSuperseded && t.ExpiresAtUtc > DateTime.UtcNow)
            .ToList();
        Assert.Single(live);
    }

    // ---- Helper: finding 1 meets finding 3 ----

    // A demoted admin's stale token must not be able to resend an invitation
    // either — the fifth write ActiveTenantAdminWrite guards, alongside
    // create/deactivate/reactivate/roles. The token is captured *while Bob is
    // still a genuine admin*, then his role is taken away — the same
    // before/after shape StaleAdminTokenAuthorizationTests uses; signing in
    // again after the demotion would just prove the mundane "a Member is
    // forbidden" case, not the stale-token one.
    [Fact]
    public async Task ADemotedAdminsStaleTokenCannotReissueAnInvitation()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var (bobId, bobToken) = await CreateSignedInAdminAsync(admin);
        var (targetId, _) = await CreateUnactivatedUserAsync(admin);

        (await admin.PutAsJsonAsync($"/users/{bobId}/roles", new { roles = new[] { "Member" } }))
            .EnsureSuccessStatusCode();

        var bobClient = _host.CreateClient();
        bobClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);

        var response = await bobClient.PostAsync($"/users/{targetId}/invitation", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Helpers ----

    private async Task<(Guid Id, string Email)> CreateUnactivatedUserAsync(HttpClient admin)
    {
        var email = $"reissue-{Guid.NewGuid():N}@acme.test";
        var created = await admin.PostAsJsonAsync("/users", new { email, fullName = "Reissue Probe" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        _createdUserIds.Add(id);
        return (id, email);
    }

    // Creates and activates a second TenantAdmin, and signs them in *before*
    // returning — the returned token is real evidence of a moment when Bob
    // genuinely was an admin, which a later step can then revoke underneath.
    private async Task<(Guid Id, string AccessToken)> CreateSignedInAdminAsync(HttpClient admin)
    {
        var email = $"bob-{Guid.NewGuid():N}@acme.test";
        var created = await admin.PostAsJsonAsync("/users", new { email, fullName = "Bob Stale" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        _createdUserIds.Add(id);

        (await admin.PutAsJsonAsync($"/users/{id}/roles", new { roles = new[] { "TenantAdmin", "Member" } }))
            .EnsureSuccessStatusCode();

        var activationToken = TokenFrom(await FindLatestSentMessageToAsync(email));
        (await _host.CreateClient().PostAsJsonAsync(
                "/auth/activate", new { token = activationToken, password = "a-perfectly-good-password" }))
            .EnsureSuccessStatusCode();

        var login = await _host.CreateClient().PostAsJsonAsync(
            "/auth/login", new { email, password = "a-perfectly-good-password" });
        login.EnsureSuccessStatusCode();
        var accessToken = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;

        return (id, accessToken);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = SeedData.SeedPassword });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<User> FindUserByEmailAsync(string email)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email);
    }

    private async Task<List<ActivationToken>> FindTokensForAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.ActivationTokens.Where(t => t.UserId == userId).ToListAsync();
    }

    // The newest message for a recipient — a reissue means a second .eml
    // exists for the same address, and the token has to come from the latest
    // one.
    private async Task<MimeMessage> FindLatestSentMessageToAsync(string recipient)
    {
        await using var scope = _host.CreateScope();
        var directory = scope.ServiceProvider
            .GetRequiredService<IOptions<EmailOptions>>().Value.DevelopmentSink.Directory;

        MimeMessage? latest = null;
        DateTime latestWriteTime = DateTime.MinValue;

        foreach (var path in Directory.GetFiles(directory, "*.eml"))
        {
            var message = await MimeMessage.LoadAsync(path);
            if (!message.To.Mailboxes.Any(m =>
                    string.Equals(m.Address, recipient, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime >= latestWriteTime)
            {
                latest = message;
                latestWriteTime = writeTime;
            }
        }

        return latest ?? throw new InvalidOperationException($"No message was written to the sink for {recipient}.");
    }

    private static string TokenFrom(MimeMessage message)
    {
        var match = Regex.Match(message.TextBody ?? string.Empty, @"token=(\S+)");
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException("No activation link found in the message.");
    }
}
