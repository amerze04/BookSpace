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

// Admin console phase 1: GET /users through the real pipeline — real JwtBearer
// handler, real authorization policies, real EF query filter and RLS, real
// ValidationBehavior and GlobalExceptionHandler.
//
// This is the only place decision `0018`'s eligibility rule can actually be
// exercised, and after this phase it is the only place at all: the rule moved
// out of memory and into SQL (see UserRepository), so a unit test over a fake
// would now be testing a predicate the database never runs.
//
// The seeded dataset gives each tenant exactly four users:
//   admin@<domain>     "Tenant Admin"      TenantAdmin   eligible
//   approver@<domain>  "Resource Approver" Approver      eligible
//   member1@<domain>   "Member One"        Member        NOT eligible
//   member2@<domain>   "Member Two"        Member        NOT eligible
// plus one SysAdmin with no OrgId at all, above every tenant.
//
// So "two of five" is the number every test below is really asserting, and each
// of the three exclusions — role, tenant, active — has a test of its own.
[Collection(nameof(AuthenticationTestCollection))]
public class UserReadEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    public UserReadEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // ---- What the endpoint is actually for ----

    // The whole reason GET /users exists: PUT /resources/{id}/approvers takes
    // user ids, and nothing in the API listed users. The answer has to be the
    // set that endpoint will accept — anyone else offered here comes back
    // ApproverNotEligible with no explanation available (`0018` collapses all
    // three reasons into that one code).
    [Fact]
    public async Task List_ReturnsOnlyUsersEligibleToApprove()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(
            ["admin@acme.test", "approver@acme.test"],
            page.Items.Select(u => u.Email).Order());
    }

    // The exclusion that makes this more than a user directory. A Member is a
    // real, active user of this tenant and must not appear: assigning one would
    // be refused by ReplaceApprovers, and `0018` would not say why.
    [Fact]
    public async Task List_ExcludesMembers()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client);

        Assert.DoesNotContain("member1@acme.test", page.Items.Select(u => u.Email));
        Assert.DoesNotContain("member2@acme.test", page.Items.Select(u => u.Email));
    }

    // Approver *or* TenantAdmin, matching AuthorizationPolicies.Approver: the set
    // that may be assigned and the set that may actually approve have to be the
    // same one, or an admin could be refused assignment to a resource they are
    // nonetheless entitled to approve (owner's call, 2026-09-01).
    [Fact]
    public async Task List_IncludesTenantAdminsAsWellAsApprovers()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client);

        var admin = page.Items.Single(u => u.Email == "admin@acme.test");
        var approver = page.Items.Single(u => u.Email == "approver@acme.test");

        Assert.Contains(Role.TenantAdmin, admin.Roles);
        Assert.Contains(Role.Approver, approver.Roles);
    }

    // Roles are on the wire because the picker has nothing else true to say
    // about eligibility, and they are serialized by name — Program.cs registers
    // JsonStringEnumConverter app-wide, so a client reads "TenantAdmin", not 1.
    [Fact]
    public async Task List_SerializesRolesByName()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var json = await client.GetStringAsync("/users");

        Assert.Contains("\"TenantAdmin\"", json);
        Assert.DoesNotContain("\"roles\":[1]", json);
    }

    // The third exclusion. A deactivated approver would silently stall every
    // approval routed to them, which is why `0018` requires IsActive — and why
    // the picker must never offer one. Restored afterwards whether or not the
    // assertions pass: the row outliving this test would quietly change what
    // every other test in the class expects.
    [Fact]
    public Task List_ExcludesDeactivatedUsers() =>
        WithDeactivatedAsync(AcmeApprover, async () =>
        {
            var client = await AuthenticatedClientAsync(AcmeAdmin);

            var page = await ListAsync(client);

            Assert.Equal(1, page.TotalCount);
            Assert.DoesNotContain(AcmeApprover, page.Items.Select(u => u.Email));
        });

    // ---- AC-4, tenant isolation ----

    // Structural, not a WHERE clause written in the repository (CLAUDE.md §4.2):
    // the query goes through the tenant-filtered DbSet, so Globex's admin cannot
    // see Acme's approver even though both are eligible in their own tenants.
    [Fact]
    public async Task List_NeverCrossesTenants()
    {
        var client = await AuthenticatedClientAsync(GlobexAdmin);

        var page = await ListAsync(client);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, u => Assert.EndsWith("@globex.test", u.Email));
    }

    // Users.OrgId is null for a SysAdmin, so the tenant filter excludes them from
    // every tenant's list — the row is not merely hidden from this endpoint, it
    // is invisible to any tenant-scoped query (PRD §2).
    [Fact]
    public async Task List_NeverIncludesThePlatformSysAdmin()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client);

        Assert.DoesNotContain(SysAdmin, page.Items.Select(u => u.Email));
    }

    // ---- Authorization ----

    // WP-3's AC in the admin console's own terms: this is an administrative
    // view, so nobody below TenantAdmin reaches it. An Approver is refused too —
    // they act on requests, they do not decide who else may.
    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task List_IsForbiddenBelowTenantAdmin(string email)
    {
        var client = await AuthenticatedClientAsync(email);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The reason both policies are stacked on the controller. SysAdmin satisfies
    // AuthorizationPolicies.TenantAdmin by role, and without TenantMember's
    // orgId-claim requirement this endpoint would answer them 200 with an empty
    // page — a confusing way to say "not yours to read" (decision `0012`).
    [Fact]
    public async Task List_IsForbiddenForSysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_IsUnauthorizedWithoutAToken()
    {
        var response = await _host.CreateClient().GetAsync("/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- The paged envelope (docs/decisions/0015) ----

    [Fact]
    public async Task List_ReturnsThePagedEnvelopeWithItsDefaults()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client);

        Assert.Equal(PagingDefaults.Page, page.Page);
        Assert.Equal(PagingDefaults.PageSize, page.PageSize);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasPreviousPage);
        Assert.False(page.HasNextPage);
    }

    // The failure the ThenBy(Id) tiebreak and ToPagedResultAsync's ordering guard
    // exist to prevent: offset paging over a non-unique order can silently
    // overlap or skip rows, and two people can share a name.
    //
    // It also pins the thing that would break if the eligibility predicate ever
    // moved back out of SQL — a TotalCount of 2 over pages of 1 proves the count
    // ran over the *eligible* set, not over all four seeded users.
    [Fact]
    public async Task List_PagesWithoutOverlappingAndKeepsTheEligibleTotal()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var first = await ListAsync(client, "?page=1&pageSize=1");
        var second = await ListAsync(client, "?page=2&pageSize=1");

        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        Assert.Equal(2, second.TotalPages);
        Assert.True(first.HasNextPage);
        Assert.False(second.HasNextPage);
        Assert.NotEqual(first.Items.Single().Id, second.Items.Single().Id);
    }

    // ---- Sorting ----

    // Full name ascending, the order a picker reads in and the order
    // FindApproverSummariesAsync already returns the assigned list in — the two
    // halves of the approvers screen agreeing.
    [Fact]
    public async Task List_DefaultOrder_IsByFullNameAscending()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client);

        Assert.Equal(["Resource Approver", "Tenant Admin"], page.Items.Select(u => u.FullName));
    }

    [Fact]
    public async Task List_DescendingSort_ReversesTheOrder()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?sort=-fullName");

        Assert.Equal(["Tenant Admin", "Resource Approver"], page.Items.Select(u => u.FullName));
    }

    // Sorting by email is a different order from sorting by name here —
    // "admin@" before "approver@", but "Resource Approver" before "Tenant
    // Admin" — so this proves the field is honoured rather than ignored.
    [Fact]
    public async Task List_SortByEmail_IsADifferentOrderFromName()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?sort=email");

        Assert.Equal(["admin@acme.test", "approver@acme.test"], page.Items.Select(u => u.Email));
    }

    // ---- Search ----

    [Fact]
    public async Task List_SearchMatchesFullName()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?search=Resource");

        Assert.Equal("approver@acme.test", Assert.Single(page.Items).Email);
    }

    [Fact]
    public async Task List_SearchMatchesEmail()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?search=admin@");

        Assert.Equal("admin@acme.test", Assert.Single(page.Items).Email);
    }

    // Search narrows the eligible set, it does not reopen it: a term that matches
    // a Member exactly still returns nothing, because eligibility is applied
    // first and is not a filter the caller can influence.
    [Fact]
    public async Task List_SearchCannotSurfaceAnIneligibleUser()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?search=member1@acme.test");

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    // Whitespace-only is "no search", the same non-decision GET /resources
    // makes — an empty text filter is meaningless, not a filter matching nothing.
    [Fact]
    public async Task List_BlankSearch_IsNoFilterAtAll()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?search=%20%20");

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task List_SearchWithNoMatch_IsAnEmptyPageNotAnError()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await ListAsync(client, "?search=nobody-by-that-name");

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    // ---- Validation (rejected, never clamped) ----

    [Theory]
    [InlineData("?pageSize=101")]
    [InlineData("?page=0")]
    [InlineData("?sort=roles")]
    [InlineData("?sort=passwordHash")]
    public async Task List_RefusesAnOutOfContractQuery(string query)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.GetAsync($"/users{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    private static async Task<PagedResult<ListUsersQueryResponse>> ListAsync(
        HttpClient client, string query = "")
    {
        var page = await client.GetFromJsonAsync<PagedResult<ListUsersQueryResponse>>(
            $"/users{query}", TestJson.Options);

        return page!;
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = SeedData.SeedPassword });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // Deactivates a seeded user, runs the assertions, and reactivates whether or
    // not they passed. Through the domain method rather than raw SQL — User owns
    // its own IsActive transition, and nothing here needs a write path the
    // application does not have.
    //
    // TenantBypassScope plus IgnoreQueryFilters, the same pair every fixture in
    // this suite uses: this scope has no HttpContext, so ICurrentTenant.OrgId is
    // null and an unaided query would match no rows at all.
    private async Task WithDeactivatedAsync(string email, Func<Task> assertions)
    {
        await SetActiveAsync(email, isActive: false);

        try
        {
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

        var user = await context.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
        var now = DateTime.UtcNow;

        if (isActive)
        {
            user.Reactivate(user.Id, now);
        }
        else
        {
            user.Deactivate(user.Id, now);
        }

        await context.SaveChangesAsync();
    }
}
