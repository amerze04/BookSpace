using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users.GetUserById;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Users;

// `GET /users/{id}` — user management phase 7, added for the user detail
// screen. Nothing else on UsersController answers a single user, and the
// directory has no id filter, so a direct link, a bookmark, or a reload had
// nothing to load from.
//
// Same seeded dataset as UserReadEndpointTests/UserDirectoryEndpointTests:
//   admin@<domain>     "Tenant Admin"      TenantAdmin
//   approver@<domain>  "Resource Approver" Approver
//   member1@<domain>   "Member One"        Member
//   member2@<domain>   "Member Two"        Member (permanently deactivated by
//                                          AuthenticationEndpointTests)
[Collection(nameof(AuthenticationTestCollection))]
public class GetUserByIdEndpointTests
{
    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private readonly AuthenticationTestHost _host;

    public GetUserByIdEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task ReturnsTheUsersDetail()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var approverId = await FindIdAsync(client, AcmeApprover);

        var detail = await GetAsync(client, approverId);

        Assert.Equal(approverId, detail.Id);
        Assert.Equal("Resource Approver", detail.FullName);
        Assert.Equal(AcmeApprover, detail.Email);
        Assert.True(detail.IsActive);
        Assert.Equal([Role.Approver], detail.Roles);
    }

    // Unlike the directory's default scope, this endpoint has no eligibility
    // filter at all — a Member is readable by id just as an admin is. The
    // detail screen is not the picker.
    [Fact]
    public async Task ReturnsAMemberJustAsReadilyAsAnAdmin()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var memberId = await FindIdAsync(client, AcmeMember);

        var detail = await GetAsync(client, memberId);

        Assert.Equal(AcmeMember, detail.Email);
        Assert.Equal([Role.Member], detail.Roles);
    }

    // A deactivated user is still readable by id — only the directory's default
    // *view* hides one, and this is not that.
    [Fact]
    public async Task ADeactivatedUserIsStillReadable()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var approverId = await FindIdAsync(client, AcmeApprover);

        await WithDeactivatedAsync(AcmeApprover, async () =>
        {
            var detail = await GetAsync(client, approverId);

            Assert.False(detail.IsActive);
        });
    }

    // ---- AC-4, tenant isolation ----

    // Structural, not a WHERE clause here: the query goes through the
    // tenant-filtered DbSet, so a real id belonging to another tenant is
    // indistinguishable from one that exists nowhere.
    [Fact]
    public async Task AnotherTenantsRealIdIsNotFound()
    {
        var globexAdminClient = await AuthenticatedClientAsync(GlobexAdmin);
        var globexAdminId = await FindIdAsync(globexAdminClient, GlobexAdmin);

        var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await acmeClient.GetAsync($"/users/{globexAdminId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownIdIsNotFound()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.GetAsync($"/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The same reason code and status as a genuinely unknown id — AC-4 forbids
    // the two being distinguishable.
    [Fact]
    public async Task AnotherTenantsIdAndAnUnknownIdAnswerIdentically()
    {
        var globexAdminClient = await AuthenticatedClientAsync(GlobexAdmin);
        var globexAdminId = await FindIdAsync(globexAdminClient, GlobexAdmin);

        var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);

        var crossTenantResponse = await acmeClient.GetAsync($"/users/{globexAdminId}");
        var unknownResponse = await acmeClient.GetAsync($"/users/{Guid.NewGuid()}");

        Assert.Equal(unknownResponse.StatusCode, crossTenantResponse.StatusCode);
        // traceId/correlationId are per-request and expected to differ; everything
        // else on the ProblemDetails body must not, matching
        // CreateUserEndpointTests' own helper for the same comparison.
        Assert.Equal(
            await ReadBodyWithoutRequestIdsAsync(unknownResponse),
            await ReadBodyWithoutRequestIdsAsync(crossTenantResponse));
    }

    // ---- Authorization ----

    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task IsForbiddenBelowTenantAdmin(string email)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var someUserId = await FindIdAsync(adminClient, AcmeAdmin);

        var client = await AuthenticatedClientAsync(email);

        var response = await client.GetAsync($"/users/{someUserId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IsForbiddenForSysAdmin()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var someUserId = await FindIdAsync(adminClient, AcmeAdmin);

        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.GetAsync($"/users/{someUserId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IsUnauthorizedWithoutAToken()
    {
        var response = await _host.CreateClient().GetAsync($"/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Helpers ----

    private static async Task<GetUserByIdQueryResponse> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/users/{id}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetUserByIdQueryResponse>(TestJson.Options))!;
    }

    private static async Task<Guid> FindIdAsync(HttpClient client, string email)
    {
        var page = await client.GetFromJsonAsync<PagedResult<ListUsersQueryResponse>>(
            "/users?scope=All&pageSize=100", TestJson.Options);

        return page!.Items.Single(u => u.Email == email).Id;
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

    private static async Task<string> ReadBodyWithoutRequestIdsAsync(HttpResponseMessage response)
    {
        var body = System.Text.Json.Nodes.JsonNode
            .Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("correlationId");
        body.Remove("traceId");
        return body.ToJsonString();
    }

    // Deactivates a seeded user, runs the assertions, and reactivates whether or
    // not they passed — the host and its database are shared across this
    // collection, matching UserDirectoryEndpointTests' own helper.
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
