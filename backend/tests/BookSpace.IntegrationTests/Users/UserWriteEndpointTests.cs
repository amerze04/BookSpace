using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Users;

// The three phase-5 writes through the real pipeline.
//
// **The test this file exists for is the last one: two admins removing each
// other's TenantAdmin role at the same moment.** The guard is a read-check-write
// across two rows, so without `UPDLOCK, HOLDLOCK` inside the write's own
// transaction both requests read "there are two admins", both pass, and the
// tenant ends with none — which no API in this system can then repair. A unit
// test cannot show that: a fake has no transaction to hold a lock in, and one
// request at a time looks identical either way. This is the AC-1 pattern applied
// to a different invariant.
//
// Everything else here works on **users this file creates and deletes**, never
// the seeded ones. Deactivating a seeded account would break other classes from
// a distance — the trap `member2@acme.test` already records — and Acme's user
// count is asserted by TenantIsolationTests.
[Collection(nameof(AuthenticationTestCollection))]
public class UserWriteEndpointTests : IAsyncLifetime
{
    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _createdUserIds = [];

    public UserWriteEndpointTests(AuthenticationTestHost host)
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

    // ---- Deactivate ----

    [Fact]
    public async Task Deactivate_Returns200WithTheFlagFlipped()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        var response = await client.PostAsync($"/users/{user}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isActive").GetBoolean());
    }

    // FR-2.4, end to end and through the real auth stack: the write this phase
    // adds is the one the login check has been waiting for since WP-2.
    [Fact]
    public async Task Deactivate_StopsTheUserLoggingIn()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, email, password) = await CreateActivatedUserAsync();

        var before = await _host.CreateClient().PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        (await client.PostAsync($"/users/{id}/deactivate", null)).EnsureSuccessStatusCode();

        var after = await _host.CreateClient().PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // FR-2.4's exact wording — "loses access immediately on next token refresh"
    // — and the family goes with it, so there is no route back to a session.
    [Fact]
    public async Task Deactivate_EndsAnExistingSessionAtItsNextRefresh()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, email, password) = await CreateActivatedUserAsync();

        var login = await _host.CreateClient().PostAsJsonAsync("/auth/login", new { email, password });
        var refreshToken = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refreshToken").GetString();

        (await client.PostAsync($"/users/{id}/deactivate", null)).EnsureSuccessStatusCode();

        var refresh = await _host.CreateClient().PostAsJsonAsync("/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var body = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AccountInactive", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Deactivate_IsIdempotent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        (await client.PostAsync($"/users/{user}/deactivate", null)).EnsureSuccessStatusCode();
        var first = await ReadUpdatedAtAsync(user);

        var second = await client.PostAsync($"/users/{user}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // A no-op must not move UpdatedAtUtc, or "last changed" starts meaning
        // "last asked about".
        Assert.Equal(first, await ReadUpdatedAtAsync(user));
    }

    [Fact]
    public async Task Deactivate_AnUnknownIdIs404()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsync($"/users/{Guid.NewGuid()}/deactivate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UserNotFound", body.GetProperty("reasonCode").GetString());
    }

    // AC-4: another tenant's real id must be indistinguishable from one that
    // exists nowhere, byte for byte.
    [Fact]
    public async Task Deactivate_AnotherTenantsUserIs404AndLooksTheSame()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var globexUser = await FindUserByEmailAsync(GlobexAdmin);

        var crossTenant = await client.PostAsync($"/users/{globexUser.Id}/deactivate", null);
        var nonexistent = await client.PostAsync($"/users/{Guid.NewGuid()}/deactivate", null);

        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(
            await ReadBodyWithoutRequestIdsAsync(nonexistent),
            await ReadBodyWithoutRequestIdsAsync(crossTenant));

        // And it really is still active — the 404 was a refusal, not a silent
        // success reported badly.
        Assert.True((await FindUserByEmailAsync(GlobexAdmin)).IsActive);
    }

    // ---- Reactivate ----

    [Fact]
    public async Task Reactivate_BringsTheAccountBack()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var (id, email, password) = await CreateActivatedUserAsync();

        (await client.PostAsync($"/users/{id}/deactivate", null)).EnsureSuccessStatusCode();
        var response = await client.PostAsync($"/users/{id}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isActive").GetBoolean());

        var login = await _host.CreateClient().PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Reactivate_IsIdempotent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);
        var before = await ReadUpdatedAtAsync(user);

        var response = await client.PostAsync($"/users/{user}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await ReadUpdatedAtAsync(user));
    }

    // ---- Roles ----

    [Fact]
    public async Task ReplaceRoles_StoresTheNewSet()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        var response = await PutRolesAsync(client, user, ["Approver", "Member"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Role.Approver, Role.Member], await ReadRolesAsync(user));
    }

    // Replace-the-set: what is not in the request is gone.
    [Fact]
    public async Task ReplaceRoles_RemovesWhatWasNotSent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Approver, Role.Member);

        await PutRolesAsync(client, user, ["Member"]);

        Assert.Equal([Role.Member], await ReadRolesAsync(user));
    }

    // The visible consequence, through a different endpoint: `GET /users` with
    // no scope is the decision `0018` eligible set, so granting Approver puts
    // somebody into the approvers picker.
    [Fact]
    public async Task ReplaceRoles_GrantingApproverMakesThemEligible()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        Assert.DoesNotContain(user, await EligibleApproverIdsAsync(client));

        await PutRolesAsync(client, user, ["Approver"]);

        Assert.Contains(user, await EligibleApproverIdsAsync(client));
    }

    [Fact]
    public async Task ReplaceRoles_AnEmptySetIs400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutRolesAsync(client, user, [])).StatusCode);
    }

    // **The privilege-escalation guard**: a TenantAdmin must not be able to
    // grant themselves the platform role by editing a request body. Nothing else
    // stops it — CK_UserRoles_Role allows the value, because the bootstrap
    // SysAdmin row needs it.
    [Fact]
    public async Task ReplaceRoles_AssigningSysAdminIs400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PutRolesAsync(client, user, ["SysAdmin"])).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PutRolesAsync(client, user, ["Member", "SysAdmin"])).StatusCode);

        Assert.Equal([Role.Member], await ReadRolesAsync(user));
    }

    [Fact]
    public async Task ReplaceRoles_AnUnknownRoleIs400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PutRolesAsync(client, user, ["Wizard"])).StatusCode);
    }

    [Fact]
    public async Task ReplaceRoles_DuplicatesAre400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var user = await CreateUserAsync(Role.Member);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PutRolesAsync(client, user, ["Member", "Member"])).StatusCode);
    }

    [Fact]
    public async Task ReplaceRoles_AnotherTenantsUserIs404()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var globexUser = await FindUserByEmailAsync(GlobexAdmin);

        var response = await PutRolesAsync(client, globexUser.Id, ["Member"]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(Role.TenantAdmin, (await FindUserByEmailAsync(GlobexAdmin)).Roles);
    }

    // ---- The last-admin guard ----

    // Acme seeds exactly one TenantAdmin, so the seeded admin *is* the last one.
    // This is the one place a seeded account is touched, and it is safe because
    // the whole point is that the write is refused.
    [Fact]
    public async Task DeactivatingTheLastAdminIs422()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var admin = await FindUserByEmailAsync(AcmeAdmin);

        var response = await client.PostAsync($"/users/{admin.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LastTenantAdmin", body.GetProperty("reasonCode").GetString());
        Assert.True((await FindUserByEmailAsync(AcmeAdmin)).IsActive);
    }

    // The second door into the same invariant: keep the account active, drop
    // the role.
    [Fact]
    public async Task TakingTheRoleFromTheLastAdminIs422()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var admin = await FindUserByEmailAsync(AcmeAdmin);

        var response = await PutRolesAsync(client, admin.Id, ["Member"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(Role.TenantAdmin, (await FindUserByEmailAsync(AcmeAdmin)).Roles);
    }

    // Both doors report the same thing, so a client can handle one case.
    [Fact]
    public async Task BothDoorsReportLastTenantAdminIdentically()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var admin = await FindUserByEmailAsync(AcmeAdmin);

        var deactivate = await client.PostAsync($"/users/{admin.Id}/deactivate", null);
        var roles = await PutRolesAsync(client, admin.Id, ["Member"]);

        Assert.Equal(
            await ReadBodyWithoutRequestIdsAsync(deactivate),
            await ReadBodyWithoutRequestIdsAsync(roles));
    }

    // And once there are two, the original can step down — the guard is about
    // the tenant keeping an administrator, not about protecting one person.
    [Fact]
    public async Task WithASecondAdminTheFirstMayStepDown()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var admin = await FindUserByEmailAsync(AcmeAdmin);
        var successor = await CreateUserAsync(Role.TenantAdmin);

        try
        {
            var response = await PutRolesAsync(client, admin.Id, ["Member"]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            // Restore the seeded admin whatever happened — every other class in
            // this collection needs them.
            await RestoreAdminAsync(admin.Id);
        }

        Assert.Contains(Role.TenantAdmin, (await FindUserAsync(successor)).Roles);
    }

    // Two admins removing each other's role at once, over real HTTP.
    //
    // **This does not prove the lock, and saying so matters.** Measured
    // 2026-09-24: with the `UPDLOCK, HOLDLOCK` hints stripped out it still
    // passes, five runs out of five — the window between the guard's read and
    // its write is too narrow for two TestServer requests to interleave
    // reliably. What it does prove is that the endpoint behaves correctly under
    // genuine parallelism, which is worth having; the mechanism is proven
    // deterministically in UserLastAdminGuardConcurrencyTests, which forces the
    // interleaving instead of hoping for it.
    //
    // Kept rather than deleted for the reason WP-7 Phase 6 recorded: a green
    // test that asserts the wrong thing is worse than none, but a green test
    // that asserts a real (narrower) thing is fine once it says which.
    //
    // Exactly one must succeed; the loser gets 422 or 409 (deadlock victim,
    // retried, then saw the truth). What would be wrong is two 200s.
    [Fact]
    public async Task TwoAdminsRemovingEachOtherAtOnce_LeaveTheTenantWithOne()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var seededAdmin = await FindUserByEmailAsync(AcmeAdmin);
        var secondAdmin = await CreateUserAsync(Role.TenantAdmin);

        try
        {
            var first = PutRolesAsync(await AuthenticatedClientAsync(AcmeAdmin), seededAdmin.Id, ["Member"]);
            var second = PutRolesAsync(await AuthenticatedClientAsync(AcmeAdmin), secondAdmin, ["Member"]);

            var responses = await Task.WhenAll(first, second);
            var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);

            Assert.Equal(1, succeeded);
            Assert.All(
                responses.Where(r => r.StatusCode != HttpStatusCode.OK),
                r => Assert.Contains(
                    r.StatusCode,
                    new[] { HttpStatusCode.UnprocessableEntity, HttpStatusCode.Conflict }));

            // The invariant itself, not just the status codes.
            Assert.Equal(1, await CountActiveAcmeAdminsAsync());
        }
        finally
        {
            await RestoreAdminAsync(seededAdmin.Id);
        }
    }

    // ---- Authorization ----

    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task ANonAdminCannotWrite(string email)
    {
        var client = await AuthenticatedClientAsync(email);
        var target = await FindUserByEmailAsync(AcmeMember);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync($"/users/{target.Id}/deactivate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync($"/users/{target.Id}/reactivate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PutRolesAsync(client, target.Id, ["Member"])).StatusCode);
    }

    [Fact]
    public async Task ASysAdminCannotWrite()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);
        var target = await FindUserByEmailAsync(AcmeMember);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync($"/users/{target.Id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCallerIs401()
    {
        var client = _host.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/users/{Guid.NewGuid()}/deactivate", null)).StatusCode);
    }

    // ---- Helpers ----

    private static Task<HttpResponseMessage> PutRolesAsync(
        HttpClient client,
        Guid userId,
        string[] roles) =>
        client.PutAsJsonAsync($"/users/{userId}/roles", new { roles });

    // Creates a user through the real POST /users and then sets the roles it
    // needs, so the fixture goes through the same write paths everything else
    // does rather than reaching into the database to invent a state.
    private async Task<Guid> CreateUserAsync(params Role[] roles)
    {
        var (id, _, _) = await CreateUserCoreAsync(roles);
        return id;
    }

    // The same, plus activation, for the tests that need the person to be able
    // to log in.
    private async Task<(Guid Id, string Email, string Password)> CreateActivatedUserAsync()
    {
        const string password = "a-perfectly-good-password";
        var (id, email, activationLink) = await CreateUserCoreAsync([Role.Member]);

        var token = activationLink.Split("token=")[1];
        (await _host.CreateClient().PostAsJsonAsync("/auth/activate", new { token, password }))
            .EnsureSuccessStatusCode();

        return (id, email, password);
    }

    private async Task<(Guid Id, string Email, string ActivationLink)> CreateUserCoreAsync(Role[] roles)
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);
        var email = $"write-probe-{Guid.NewGuid():N}@acme.test";

        var created = await admin.PostAsJsonAsync("/users", new { email, fullName = "Write Probe" });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        _createdUserIds.Add(id);

        // POST /users always creates a Member; anything else is a role write.
        if (!(roles.Length == 1 && roles[0] == Role.Member))
        {
            (await PutRolesAsync(admin, id, roles.Select(r => r.ToString()).ToArray()))
                .EnsureSuccessStatusCode();
        }

        return (id, email, body.GetProperty("activationLink").GetString()!);
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

    private async Task<IEnumerable<Guid>> EligibleApproverIdsAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/users?pageSize=100", TestJson.Options);
        return response.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
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

    private async Task<DateTime> ReadUpdatedAtAsync(Guid id) => (await FindUserAsync(id)).UpdatedAtUtc;

    private async Task<IReadOnlyCollection<Role>> ReadRolesAsync(Guid id) =>
        (await FindUserAsync(id)).Roles.OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();

    private async Task<int> CountActiveAcmeAdminsAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var acmeOrgId = (await context.Users
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Email == AcmeAdmin)).OrgId;

        var users = await context.Users
            .IgnoreQueryFilters()
            .Where(u => u.OrgId == acmeOrgId && u.IsActive)
            .ToListAsync();

        return users.Count(u => u.Roles.Contains(Role.TenantAdmin));
    }

    // Puts the seeded admin back exactly as SeedData left them. Several classes
    // in this collection log in as them, so a distance-failure here is the worst
    // kind — and this method has already caused one.
    //
    // **ReplaceRoles, not AddRole.** The first version added TenantAdmin back on
    // top of whatever the test had just written, so an admin the test had set to
    // `["Member"]` came out as `[Member, TenantAdmin]` — which passed everything
    // here and failed one assertion in UserDirectoryEndpointTests, in a full run
    // only. Restoring state means restoring the *set*, not topping it up.
    private async Task RestoreAdminAsync(Guid adminId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var admin = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == adminId);
        admin.Reactivate(admin.Id, DateTime.UtcNow);
        admin.ReplaceRoles([Role.TenantAdmin], admin.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
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
