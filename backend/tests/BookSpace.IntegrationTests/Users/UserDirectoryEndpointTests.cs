using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Users;

// `GET /users?scope=All` — user management phase 4.
//
// **UserReadEndpointTests is the regression proof for this phase and is
// deliberately untouched by it.** Every test in that file sends no scope and
// must keep getting the decision `0018` eligible-approver set, byte for byte;
// this file only covers what the new parameter adds. If widening had been done
// by changing the default instead, that file would have had to be rewritten —
// which is exactly the signal docs/user-management-plan.md §4.6 wanted to keep.
//
// The seeded dataset gives each tenant exactly four users:
//   admin@<domain>     "Tenant Admin"      TenantAdmin   eligible
//   approver@<domain>  "Resource Approver" Approver      eligible
//   member1@<domain>   "Member One"        Member        NOT eligible
//   member2@<domain>   "Member Two"        Member        NOT eligible
// plus one SysAdmin with no OrgId at all, above every tenant.
//
// So the two scopes answer 2 and 4 over the same tenant, and most of what
// follows is that difference and its edges.
//
// **One trap this file walked straight into, and the first file where it bites:
// `member2@acme.test` is permanently deactivated** by
// AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin, deliberately
// and with its sibling's comment saying so outright ("that one sacrifices
// member2@acme.test permanently"). BookingReadEndpointTests already records
// hitting it. No previous test could see the difference — the eligible set
// excludes Members anyway — but `scope=All` returns that row, and an assertion
// that "every user is active" therefore passed alone and failed only in a full
// run. Tests here that care about IsActive name the account they mean, and
// admin@acme.test is the one nothing in the suite deactivates.
[Collection(nameof(AuthenticationTestCollection))]
public class UserDirectoryEndpointTests
{
    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private readonly AuthenticationTestHost _host;

    public UserDirectoryEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // ---- What the wider scope adds ----

    [Fact]
    public async Task AllScope_ReturnsEveryUserInTheTenant()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&pageSize=100");

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(
            ["admin@acme.test", "approver@acme.test", "member1@acme.test", "member2@acme.test"],
            page.Items.Select(u => u.Email).Order());
    }

    // The exclusion the default scope makes and this one must not: a Member is a
    // real user of the tenant, and a directory that hid them would be useless.
    [Fact]
    public async Task AllScope_IncludesMembers()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&pageSize=100");

        Assert.Contains(AcmeMember, page.Items.Select(u => u.Email));
    }

    // The other exclusion, and the one that needed IsActive on the row: without
    // it an admin cannot tell somebody who left from somebody never added.
    [Fact]
    public async Task AllScope_IncludesDeactivatedUsers()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        await WithDeactivatedAsync(AcmeApprover, async () =>
        {
            var page = await ListAsync(client, "?scope=All&pageSize=100");

            Assert.Equal(4, page.TotalCount);
            var deactivated = page.Items.Single(u => u.Email == AcmeApprover);
            Assert.False(deactivated.IsActive);
        });
    }

    // Named, not "every row" — see the note about member2@acme.test at the top
    // of this file. admin@acme.test is the account nothing in the suite
    // deactivates, because deactivating the tenant's only admin would break
    // every other class.
    [Fact]
    public async Task AllScope_ReportsAnActiveUserAsActive()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&pageSize=100");

        Assert.True(page.Items.Single(u => u.Email == AcmeAdmin).IsActive);
    }

    [Fact]
    public async Task AllScope_CarriesEachUsersRoles()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&pageSize=100");

        Assert.Equal([Role.TenantAdmin], page.Items.Single(u => u.Email == AcmeAdmin).Roles);
        Assert.Equal([Role.Approver], page.Items.Single(u => u.Email == AcmeApprover).Roles);
        Assert.Equal([Role.Member], page.Items.Single(u => u.Email == AcmeMember).Roles);
    }

    // ---- What it must not add ----

    // The scope widens *within* a tenant and nowhere else. It is not a filter
    // this parameter could relax — the query filter and RLS are what exclude
    // another tenant's rows (CLAUDE.md §4.2), and no value here reaches them.
    [Fact]
    public async Task AllScope_StillStopsAtTheTenantBoundary()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&pageSize=100");

        Assert.DoesNotContain(page.Items, u => u.Email.EndsWith("@globex.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllScope_AnswersEachTenantWithItsOwnUsers()
    {
        var acme = await ListAsync(await AuthenticatedClientAsync(AcmeAdmin), "?scope=All&pageSize=100");
        var globex = await ListAsync(await AuthenticatedClientAsync(GlobexAdmin), "?scope=All&pageSize=100");

        Assert.Equal(4, acme.TotalCount);
        Assert.Equal(4, globex.TotalCount);
        Assert.Empty(acme.Items.Select(u => u.Email).Intersect(globex.Items.Select(u => u.Email)));
    }

    // A SysAdmin has no OrgId, so they belong to nobody's tenant and the query
    // filter drops them. Worth asserting rather than assuming: PRD §2 says the
    // platform operator must never appear in tenant content.
    [Fact]
    public async Task AllScope_NeverShowsTheSysAdmin()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&pageSize=100");

        Assert.DoesNotContain(SysAdmin, page.Items.Select(u => u.Email));
    }

    // ---- The default is unchanged ----

    // Restated here as well as in UserReadEndpointTests, because it is *this*
    // phase's promise: adding a parameter changed nothing for a caller that does
    // not send it.
    [Fact]
    public async Task OmittingScope_StillAnswersTheEligibleApproverSet()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?pageSize=100");

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["admin@acme.test", "approver@acme.test"], page.Items.Select(u => u.Email).Order());
    }

    [Fact]
    public async Task OmittingScope_IsIdenticalToAskingForTheEligibleApproverSet()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var omitted = await client.GetStringAsync("/users?pageSize=100");
        var explicitScope = await client.GetStringAsync("/users?scope=EligibleApprovers&pageSize=100");

        Assert.Equal(omitted, explicitScope);
    }

    // Still true of the default scope, and now worth asserting rather than
    // stating: an inactive user is not an eligible approver, so every row of the
    // picker's answer has IsActive true even though the field now varies.
    [Fact]
    public async Task TheDefaultScope_StillHasEveryRowActive()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        await WithDeactivatedAsync(AcmeApprover, async () =>
        {
            var page = await ListAsync(client, "?pageSize=100");

            Assert.Equal(1, page.TotalCount);
            Assert.All(page.Items, u => Assert.True(u.IsActive));
        });
    }

    // ---- Paging, searching and sorting work the same in both scopes ----

    [Fact]
    public async Task AllScope_SearchesNameAndEmail()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var byName = await ListAsync(client, "?scope=All&search=Member One");
        var byEmail = await ListAsync(client, "?scope=All&search=member1@");

        Assert.Equal(AcmeMember, Assert.Single(byName.Items).Email);
        Assert.Equal(AcmeMember, Assert.Single(byEmail.Items).Email);
    }

    // The reason the predicate had to move into SQL in admin console phase 1:
    // filtering after OFFSET/FETCH would page over the wrong set and report a
    // TotalCount describing a different one. The same has to hold for the wider
    // scope.
    [Fact]
    public async Task AllScope_PagesOverTheWholeSet()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var first = await ListAsync(client, "?scope=All&pageSize=2&page=1");
        var second = await ListAsync(client, "?scope=All&pageSize=2&page=2");

        Assert.Equal(4, first.TotalCount);
        Assert.Equal(4, second.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(u => u.Id).Intersect(second.Items.Select(u => u.Id)));
    }

    [Fact]
    public async Task AllScope_SortsByEmailWhenAsked()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?scope=All&sort=email&pageSize=100");

        Assert.Equal(page.Items.Select(u => u.Email).Order(), page.Items.Select(u => u.Email));
    }

    // ---- Refusals ----

    // Bound by name, so an unrecognized word is a model-binding failure rather
    // than a silently narrowed answer.
    [Theory]
    [InlineData("Everyone")]
    [InlineData("all-users")]
    [InlineData("99")]
    public async Task AnUnrecognizedScopeIs400(string scope)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.GetAsync($"/users?scope={scope}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Scope changes nothing about who may call this at all — the whole
    // controller is TenantAdmin, and a Member asking for the directory is
    // refused for the same reason they are refused the picker.
    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task ANonAdminIsForbiddenWhicheverScopeTheyAsk(string email)
    {
        var client = await AuthenticatedClientAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/users?scope=All")).StatusCode);
    }

    [Fact]
    public async Task ASysAdminIsForbidden()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/users?scope=All")).StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCallerIs401()
    {
        var client = _host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/users?scope=All")).StatusCode);
    }

    // ---- Helpers ----

    // TestJson.Options, not the default: the API serializes enums by name, so
    // a role arrives as "Member" and System.Text.Json's default converter reads
    // numbers only.
    private static async Task<PagedResult<ListUsersQueryResponse>> ListAsync(
        HttpClient client,
        string queryString)
    {
        var response = await client.GetAsync($"/users{queryString}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedResult<ListUsersQueryResponse>>(
            TestJson.Options))!;
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

    // Deactivates a seeded user, runs the assertions, and reactivates whether or
    // not they passed — the host and its database are shared across this
    // collection. Through the domain method rather than raw SQL, matching
    // UserReadEndpointTests' own helper.
    private async Task WithDeactivatedAsync(string email, Func<Task> assertions)
    {
        try
        {
            await SetActiveAsync(email, isActive: false);
            await assertions();
        }
        finally
        {
            await SetActiveAsync(email, isActive: true);
        }
    }

    private async Task SetActiveAsync(string email, bool isActive)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var user = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email);

        if (isActive)
        {
            user.Reactivate(user.Id, DateTime.UtcNow);
        }
        else
        {
            user.Deactivate(user.Id, DateTime.UtcNow);
        }

        await context.SaveChangesAsync();
    }
}
