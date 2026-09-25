using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Email;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BookSpace.IntegrationTests.Users;

// POST /users through the real pipeline — real policies, real tenant query
// filter and RLS, real UQ_Users_Email, and the real development email sink, so
// the invitation that comes out is a message a mail client could open.
//
// Two things can only be proven here. **UQ_Users_Email is the whole of the
// duplicate-email rule** — there is no pre-check anywhere in the handler, so a
// unit test has nothing to exercise but a fake throwing on command. And **the
// cross-tenant case is the one that matters**: decision `0010` makes email
// unique platform-wide, so an address in another organization has to be refused
// identically, and only a real database with both tenants in it can show that.
//
// Every test provisions into Acme and cleans up after itself; the host and its
// database are shared across this collection, and TenantIsolationTests asserts
// Acme has exactly four users.
[Collection(nameof(AuthenticationTestCollection))]
public class CreateUserEndpointTests : IAsyncLifetime
{
    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _createdUserIds = [];

    public CreateUserEndpointTests(AuthenticationTestHost host)
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
    public async Task ItAnswers201WithTheCreatedUser()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync("/users", new { email, fullName = "Ada Lovelace" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadCreatedAsync(response);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", body.GetProperty("fullName").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task TheNewUserLandsInTheCallersTenant()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var body = await CreateAsync(client, UniqueEmail());

        var created = await FindUserAsync(body.GetProperty("id").GetGuid());
        var acmeOrgId = (await FindUserByEmailAsync(AcmeAdmin)).OrgId;
        Assert.Equal(acmeOrgId, created.OrgId);
    }

    [Fact]
    public async Task TheNewUserIsAMember()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var body = await CreateAsync(client, UniqueEmail());

        var created = await FindUserAsync(body.GetProperty("id").GetGuid());
        Assert.Equal([Role.Member], created.Roles);
    }

    // The provisioning audit trail — who added this colleague.
    [Fact]
    public async Task TheNewUserRecordsWhoCreatedThem()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var body = await CreateAsync(client, UniqueEmail());

        var created = await FindUserAsync(body.GetProperty("id").GetGuid());
        var admin = await FindUserByEmailAsync(AcmeAdmin);
        Assert.Equal(admin.Id, created.CreatedByUserId);
    }

    // **The whole point of the feature, end to end**: the account the admin
    // just created can be activated with the link in the invitation email and
    // then signed into — and the admin never knew the password.
    //
    // The token comes from the sent message, not the response body — since the
    // hardening pass (2026-09-25, finding 2) POST /users no longer hands the
    // raw activation link back to the caller at all.
    [Fact]
    public async Task TheEmailedLinkActivatesTheAccount()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var email = UniqueEmail();

        await CreateAsync(client, email);
        var message = await FindSentMessageToAsync(email);
        var token = TokenFrom(ExtractActivationLink(message));

        var anonymous = _host.CreateClient();
        var activate = await anonymous.PostAsJsonAsync(
            "/auth/activate", new { token, password = "a-perfectly-good-password" });
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);

        var login = await anonymous.PostAsJsonAsync(
            "/auth/login", new { email, password = "a-perfectly-good-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // No window in which a provisioned account is sign-innable before its owner
    // activates it.
    [Fact]
    public async Task TheAccountCannotBeSignedIntoBeforeActivation()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var email = UniqueEmail();

        await CreateAsync(client, email);

        var anonymous = _host.CreateClient();
        foreach (var attempt in new[] { SeedData.SeedPassword, "a-perfectly-good-password", string.Empty })
        {
            var login = await anonymous.PostAsJsonAsync("/auth/login", new { email, password = attempt });
            Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task OnlyAHashOfTheTokenIsStored()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var email = UniqueEmail();

        var body = await CreateAsync(client, email);
        var message = await FindSentMessageToAsync(email);
        var rawToken = TokenFrom(ExtractActivationLink(message));

        var stored = await FindTokenForAsync(body.GetProperty("id").GetGuid());
        Assert.NotEqual(rawToken, stored.TokenHash);
        Assert.False(stored.IsConsumed);
    }

    [Fact]
    public async Task TheLinkPointsAtTheConfiguredActivationPage()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var email = UniqueEmail();

        await CreateAsync(client, email);
        var message = await FindSentMessageToAsync(email);

        Assert.StartsWith("https://bookspace.test/activate?token=", ExtractActivationLink(message));
    }

    // Hardening pass, 2026-09-25 (finding 2). The regression this whole change
    // exists to prevent: a TenantAdmin must never be able to read the raw
    // credential and redeem it before the invitee does.
    [Fact]
    public async Task TheResponseNeverCarriesTheRawActivationLink()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var body = await CreateAsync(client, UniqueEmail());

        Assert.False(body.TryGetProperty("activationLink", out _));
        Assert.True(body.TryGetProperty("activationLinkExpiresAtUtc", out _));
        Assert.True(body.TryGetProperty("invitationEmailSent", out _));
    }

    // ---- The invitation ----

    [Fact]
    public async Task ItReportsThatTheInvitationWasSent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var body = await CreateAsync(client, UniqueEmail());

        Assert.True(body.GetProperty("invitationEmailSent").GetBoolean());
    }

    // The sink writes a real .eml through the same MimeMessageFactory the SMTP
    // sender uses, so what is asserted here is what would have gone on the wire.
    [Fact]
    public async Task TheInvitationIsAMessageAMailClientCouldOpen()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var email = UniqueEmail();

        await CreateAsync(client, email);

        var message = await FindSentMessageToAsync(email);
        Assert.Equal("You have been invited to BookSpace", message.Subject);
        Assert.Equal(email, Assert.IsType<MailboxAddress>(Assert.Single(message.To)).Address);

        // Internal consistency: whatever link the text part carries, the HTML
        // part carries the identical one.
        var link = ExtractActivationLink(message);
        Assert.Contains(link, message.TextBody, StringComparison.Ordinal);
        Assert.Contains(link, message.HtmlBody, StringComparison.Ordinal);
    }

    // ---- The duplicate-email rule ----

    [Fact]
    public async Task AnAddressAlreadyInTheCallersTenantIsRefused()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/users", new { email = AcmeMember, fullName = "Someone Else" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EmailAlreadyInUse", problem.GetProperty("reasonCode").GetString());
    }

    // **The case only a real database can show.** Decision `0010` makes email
    // unique platform-wide, so an address held by another organization is
    // refused too — and must be refused the *same way*, or POST /users becomes
    // a cross-tenant existence oracle (AC-4, docs/user-management-plan.md §3.2).
    [Fact]
    public async Task AnAddressInAnotherTenantIsRefusedIdentically()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var ownTenant = await client.PostAsJsonAsync(
            "/users", new { email = AcmeMember, fullName = "Someone Else" });
        var otherTenant = await client.PostAsJsonAsync(
            "/users", new { email = GlobexAdmin, fullName = "Someone Else" });

        Assert.Equal(HttpStatusCode.Conflict, otherTenant.StatusCode);
        Assert.Equal(
            await ReadBodyWithoutRequestIdsAsync(ownTenant),
            await ReadBodyWithoutRequestIdsAsync(otherTenant));
    }

    // Emails are normalized on the way in (User.NormalizeEmail), so case cannot
    // be used to slip a second account past the unique index.
    [Fact]
    public async Task AnAddressDifferingOnlyInCaseIsRefused()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/users", new { email = AcmeMember.ToUpperInvariant(), fullName = "Someone Else" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // A refused insert must leave nothing behind — no half-created account, and
    // above all no activation token pointing at one.
    [Fact]
    public async Task ARefusedCreateWritesNothing()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var before = await CountAcmeUsersAsync();

        await client.PostAsJsonAsync("/users", new { email = AcmeMember, fullName = "Someone Else" });

        Assert.Equal(before, await CountAcmeUsersAsync());
    }

    // And no invitation goes out for an account that does not exist.
    [Fact]
    public async Task ARefusedCreateSendsNoInvitation()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        await client.PostAsJsonAsync("/users", new { email = AcmeMember, fullName = "Someone Else" });

        Assert.Null(await TryFindSentMessageToAsync(AcmeMember));
    }

    // ---- Validation ----

    [Theory]
    [InlineData("", "Ada Lovelace")]
    [InlineData("not-an-address", "Ada Lovelace")]
    [InlineData("ada lovelace@acme.test", "Ada Lovelace")]
    [InlineData("ada@acme.test", "")]
    public async Task AMalformedRequestIs400(string email, string fullName)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync("/users", new { email, fullName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Authorization ----

    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task ANonAdminIsForbidden(string email)
    {
        var client = await AuthenticatedClientAsync(email);

        var response = await client.PostAsJsonAsync(
            "/users", new { email = UniqueEmail(), fullName = "Ada Lovelace" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCallerIs401()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/users", new { email = UniqueEmail(), fullName = "Ada Lovelace" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The two stacked policies. TenantAdmin admits a SysAdmin by role, and
    // TenantMember is what keeps them out — they carry no orgId claim, so
    // without it this would answer 500 from the handler's own tenant guard
    // rather than the 403 it should be.
    [Fact]
    public async Task ASysAdminIsForbidden()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsJsonAsync(
            "/users", new { email = UniqueEmail(), fullName = "Ada Lovelace" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // An admin of one tenant creates into their own, and cannot reach another's
    // — there is no field on the wire that would let them try, which is the
    // point (see CreateUserCommandRequest).
    [Fact]
    public async Task AnotherTenantsAdminCreatesIntoTheirOwnTenant()
    {
        var client = await AuthenticatedClientAsync(GlobexAdmin);
        var email = $"invitee-{Guid.NewGuid():N}@globex.test";

        var body = await CreateAsync(client, email);

        var created = await FindUserAsync(body.GetProperty("id").GetGuid());
        var globexOrgId = (await FindUserByEmailAsync(GlobexAdmin)).OrgId;
        Assert.Equal(globexOrgId, created.OrgId);
    }

    // ---- Helpers ----

    private static string UniqueEmail() => $"invitee-{Guid.NewGuid():N}@acme.test";

    private async Task<JsonElement> CreateAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/users", new { email, fullName = "Ada Lovelace" });
        response.EnsureSuccessStatusCode();
        return await ReadCreatedAsync(response);
    }

    private async Task<JsonElement> ReadCreatedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        _createdUserIds.Add(body.GetProperty("id").GetGuid());
        return body;
    }

    private static string TokenFrom(string activationLink) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(activationLink).Query)["token"]!;

    // Hardening pass, 2026-09-25 (finding 2). The link now lives only in the
    // sent message, never in a response body — this is the one place it can
    // still be read from for a test that needs a real, usable token.
    private static string ExtractActivationLink(MimeMessage message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            message.TextBody ?? string.Empty, @"https://\S+/activate\?token=\S+");
        return match.Success
            ? match.Value
            : throw new InvalidOperationException("No activation link found in the message.");
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

    private async Task<User> FindUserAsync(Guid id)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == id);
    }

    private async Task<User> FindUserByEmailAsync(string email)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email);
    }

    private async Task<ActivationToken> FindTokenForAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.ActivationTokens.FirstAsync(t => t.UserId == userId);
    }

    private async Task<int> CountAcmeUsersAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var acmeOrgId = (await FindUserByEmailAsync(AcmeAdmin)).OrgId;
        return await context.Users.IgnoreQueryFilters().CountAsync(u => u.OrgId == acmeOrgId);
    }

    private async Task<MimeMessage> FindSentMessageToAsync(string recipient) =>
        await TryFindSentMessageToAsync(recipient)
        ?? throw new InvalidOperationException($"No message was written to the sink for {recipient}.");

    // The sink directory is shared by the whole run, so a message is found by
    // its recipient rather than by being the only file there.
    private async Task<MimeMessage?> TryFindSentMessageToAsync(string recipient)
    {
        await using var scope = _host.CreateScope();
        var directory = scope.ServiceProvider
            .GetRequiredService<IOptions<EmailOptions>>().Value.DevelopmentSink.Directory;

        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var path in Directory.GetFiles(directory, "*.eml"))
        {
            var message = await MimeMessage.LoadAsync(path);
            if (message.To.Mailboxes.Any(m =>
                    string.Equals(m.Address, recipient, StringComparison.OrdinalIgnoreCase)))
            {
                return message;
            }
        }

        return null;
    }

    private static async Task<string> ReadBodyWithoutRequestIdsAsync(HttpResponseMessage response)
    {
        var body = System.Text.Json.Nodes.JsonNode
            .Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("correlationId");
        body.Remove("traceId");
        return body.ToJsonString();
    }
}
