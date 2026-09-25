using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Email;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BookSpace.IntegrationTests.Users;

// Hardening pass, 2026-09-25 (finding 1). Real, issued access tokens throughout
// — not a hand-built JWT fixture — because the bug this proves is exactly
// "a token that was genuinely valid a moment ago stays accepted after the row
// it describes changed underneath it". Anything less than the real sign-in ->
// promote -> revoke -> reuse sequence would risk proving the fix works against
// a token this suite invented rather than one the API actually issues.
//
// Every scenario below shares one shape: Bob is created, promoted to
// TenantAdmin, and signs in for a real access token while that is still true.
// Only *then* does the tenant admin (or, for the DB-level steps, a direct
// write standing in for "some other admin's action already landed") revoke
// what made that token valid — deactivation or the TenantAdmin role. Bob's
// token is never refreshed or reissued; it is the same bytes from before the
// revocation, presented after it.
[Collection(nameof(AuthenticationTestCollection))]
public class StaleAdminTokenAuthorizationTests : IAsyncLifetime
{
    private const string AcmeAdmin = "admin@acme.test";
    private const string Password = "a-perfectly-good-password";

    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _createdUserIds = [];

    public StaleAdminTokenAuthorizationTests(AuthenticationTestHost host)
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

    // ---- The two scenarios from the finding, verbatim ----

    [Fact]
    public async Task ADeactivatedAdminsStaleTokenCannotReactivateThemselves()
    {
        var (bobId, bobToken) = await CreateSignedInAdminAsync();

        await DeactivateDirectlyAsync(bobId);

        var response = await ClientWithToken(bobToken).PostAsync($"/users/{bobId}/reactivate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ADemotedAdminsStaleTokenCannotRestoreTheirOwnRole()
    {
        var (bobId, bobToken) = await CreateSignedInAdminAsync();

        await RemoveTenantAdminRoleDirectlyAsync(bobId);

        var response = await ClientWithToken(bobToken).PutAsJsonAsync(
            $"/users/{bobId}/roles", new { roles = new[] { "TenantAdmin", "Member" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- The finding's third requirement: not just the two repro cases ----

    [Fact]
    public async Task ADeactivatedAdminsStaleTokenCannotCreateOtherUsers()
    {
        var (bobId, bobToken) = await CreateSignedInAdminAsync();

        await DeactivateDirectlyAsync(bobId);

        var response = await ClientWithToken(bobToken).PostAsJsonAsync(
            "/users", new { email = $"stale-create-{Guid.NewGuid():N}@acme.test", fullName = "Should Not Exist" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ADemotedAdminsStaleTokenCannotDeactivateSomeoneElse()
    {
        var (bobId, bobToken) = await CreateSignedInAdminAsync();
        var targetId = await CreateMemberAsync();

        await RemoveTenantAdminRoleDirectlyAsync(bobId);

        var response = await ClientWithToken(bobToken).PostAsync($"/users/{targetId}/deactivate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.True((await FindUserAsync(targetId)).IsActive);
    }

    // ---- The control: this must not become "no TenantAdmin token works" ----

    [Fact]
    public async Task AGenuineAdminsTokenStillWorks()
    {
        var (_, bobToken) = await CreateSignedInAdminAsync();
        var targetId = await CreateMemberAsync();

        var response = await ClientWithToken(bobToken).PostAsync($"/users/{targetId}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Reads, unlike the four writes, are deliberately NOT behind
    // ActiveTenantAdminWrite (CLAUDE.md's own "do not pay for a check you do
    // not need" — a read cannot grant or revoke anything). Pinned here so a
    // future change to the directory read is caught if it accidentally starts
    // requiring the stronger policy too, which would be a behaviour change
    // nobody asked for rather than a security fix.
    [Fact]
    public async Task ADeactivatedAdminsStaleTokenCanStillReadTheDirectory()
    {
        var (bobId, bobToken) = await CreateSignedInAdminAsync();

        await DeactivateDirectlyAsync(bobId);

        var response = await ClientWithToken(bobToken).GetAsync("/users?scope=All");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Helpers ----

    // Creates a Member, promotes them to TenantAdmin through the real API
    // (proving nothing here depends on a fixture bypassing the write path),
    // activates the account and signs in — so the returned token is exactly
    // what a real administrator's browser would be holding.
    private async Task<(Guid Id, string AccessToken)> CreateSignedInAdminAsync()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var email = $"bob-{Guid.NewGuid():N}@acme.test";

        var created = await admin.PostAsJsonAsync("/users", new { email, fullName = "Bob Stale" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        _createdUserIds.Add(id);

        (await admin.PutAsJsonAsync($"/users/{id}/roles", new { roles = new[] { "TenantAdmin", "Member" } }))
            .EnsureSuccessStatusCode();

        var message = await FindSentMessageToAsync(email);
        var token = ExtractActivationToken(message);
        (await _host.CreateClient().PostAsJsonAsync("/auth/activate", new { token, password = Password }))
            .EnsureSuccessStatusCode();

        var login = await _host.CreateClient().PostAsJsonAsync("/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var accessToken = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;

        return (id, accessToken);
    }

    private async Task<Guid> CreateMemberAsync()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var email = $"stale-target-{Guid.NewGuid():N}@acme.test";

        var created = await admin.PostAsJsonAsync("/users", new { email, fullName = "Target Person" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        _createdUserIds.Add(id);
        return id;
    }

    private HttpClient ClientWithToken(string accessToken)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // Direct writes standing in for "the tenant's real administrator already
    // acted" — the same TenantBypassScope + IgnoreQueryFilters shape every
    // other fixture in this collection uses (see UserWriteEndpointTests'
    // RestoreAdminAsync), through the domain methods rather than raw SQL.
    private async Task DeactivateDirectlyAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var user = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.Deactivate(user.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task RemoveTenantAdminRoleDirectlyAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var user = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.ReplaceRoles([Role.Member], user.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task<Domain.Entities.User> FindUserAsync(Guid id)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == id);
    }

    private async Task<Domain.Entities.User> FindUserByEmailAsync(string email)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email);
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

    // Same sink-reading approach as CreateUserEndpointTests — the API no longer
    // hands the activation link back in the response body (finding 2), so a
    // usable token can only come from the message that was actually sent.
    private async Task<MimeMessage> FindSentMessageToAsync(string recipient)
    {
        await using var scope = _host.CreateScope();
        var directory = scope.ServiceProvider
            .GetRequiredService<IOptions<EmailOptions>>().Value.DevelopmentSink.Directory;

        foreach (var path in Directory.GetFiles(directory, "*.eml"))
        {
            var message = await MimeMessage.LoadAsync(path);
            if (message.To.Mailboxes.Any(m =>
                    string.Equals(m.Address, recipient, StringComparison.OrdinalIgnoreCase)))
            {
                return message;
            }
        }

        throw new InvalidOperationException($"No message was written to the sink for {recipient}.");
    }

    private static string ExtractActivationToken(MimeMessage message)
    {
        var match = Regex.Match(message.TextBody ?? string.Empty, @"token=(\S+)");
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException("No activation link found in the message.");
    }
}
