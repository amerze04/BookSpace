using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// WP-3 Phase 2 step 2: GET /resources and GET /resources/{id} through the real
// pipeline — real JwtBearer handler, real authorization policies, real EF query
// filter and RLS, real ValidationBehavior and GlobalExceptionHandler. These are
// also the first consumer of anything Phase 1 built, so the paged envelope, the
// sort whitelist and the error contract are exercised here for the first time
// against something other than synthetic input.
//
// The seeded dataset (SeedData) gives each tenant exactly two resources:
// "Conference Room A" (Room, capacity 8) and "3D Printer" (Equipment,
// capacity 1, RequiresApproval).
[Collection(nameof(AuthenticationTestCollection))]
public class ResourceReadEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeMember = "member1@acme.test";
    private const string AcmeAdmin = "admin@acme.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    public ResourceReadEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // ---- The paged envelope (docs/decisions/0015) ----

    [Fact]
    public async Task List_ReturnsTheTenantsResourcesInThePagedEnvelope()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>("/resources");

        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(PagingDefaults.Page, page.Page);
        Assert.Equal(PagingDefaults.PageSize, page.PageSize);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasPreviousPage);
        Assert.False(page.HasNextPage);
    }

    // The point of offset paging with a total: page 2 knows there are two rows
    // overall and that it is the last page.
    [Fact]
    public async Task List_SecondPage_ReturnsTheRemainderAndKeepsTheTotal()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var first = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
            "/resources?page=1&pageSize=1");
        var second = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
            "/resources?page=2&pageSize=1");

        Assert.Equal(2, first!.TotalCount);
        Assert.Equal(2, second!.TotalCount);
        Assert.Equal(2, second.TotalPages);
        Assert.True(first.HasNextPage);
        Assert.True(second.HasPreviousPage);
        Assert.False(second.HasNextPage);

        // The pages do not overlap — the failure the ThenBy(Id) tiebreaker and
        // ToPagedResultAsync's ordering guard exist to prevent.
        Assert.NotEqual(first.Items.Single().Id, second.Items.Single().Id);
    }

    [Fact]
    public async Task List_PageBeyondTheEnd_IsEmptyButStillReportsTheTotal()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
            "/resources?page=50&pageSize=20");

        Assert.Empty(page!.Items);
        Assert.Equal(2, page.TotalCount);
    }

    // ---- Sorting ----

    [Fact]
    public async Task List_DefaultOrder_IsByNameAscending()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>("/resources");

        Assert.Equal(["3D Printer", "Conference Room A"], page!.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task List_DescendingSort_ReversesTheOrder()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>("/resources?sort=-name");

        Assert.Equal(["Conference Room A", "3D Printer"], page!.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task List_SortsByAWhitelistedFieldOtherThanName()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
            "/resources?sort=-capacity");

        Assert.Equal([8, 1], page!.Items.Select(r => r.Capacity));
    }

    // SortOption matches case-insensitively, but the whitelist owns the spelling.
    [Fact]
    public async Task List_SortFieldMatchIsCaseInsensitive()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
            "/resources?sort=-CAPACITY");

        Assert.Equal([8, 1], page!.Items.Select(r => r.Capacity));
    }

    // timeZoneId is a real response field but deliberately not sortable — the
    // whitelist is a contract, not a reflection of the columns.
    [Fact]
    public async Task List_UnknownSortField_IsRejectedWithPerFieldErrors()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync("/resources?sort=timeZoneId");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("Sort", out _));
    }

    // ---- Paging validation: rejected, not clamped (PagingDefaults) ----

    [Fact]
    public async Task List_PageSizeAboveTheMaximum_IsRejectedRatherThanClamped()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/resources?pageSize={PagingDefaults.MaxPageSize + 1}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("PageSize", out _));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    public async Task List_InvalidPaging_IsRejected(string queryString)
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/resources?{queryString}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Get by id ----

    [Fact]
    public async Task GetById_ReturnsTheFullDetailRepresentation()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);
        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>("/resources");
        var printer = page!.Items.Single(r => r.Name == "3D Printer");

        var detail = await client.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{printer.Id}");

        Assert.Equal(printer.Id, detail!.Id);
        Assert.Equal("3D Printer", detail.Name);
        Assert.Equal("Shared prototyping printer", detail.Description);
        Assert.Equal("Equipment", detail.ResourceType);
        Assert.Equal(1, detail.Capacity);
        Assert.True(detail.RequiresApproval);
        Assert.Equal(60, detail.MinDurationMinutes);
        Assert.Equal(180, detail.MaxDurationMinutes);
        Assert.False(detail.IsArchived);
        Assert.NotEqual(default, detail.CreatedAtUtc);
    }

    // The wire contract for instants, asserted on the raw JSON rather than the
    // deserialized DTO — System.Text.Json accepts an offset-less timestamp
    // happily, so a round trip through ResourceDetailResponse would not notice
    // the "Z" going missing. A browser client parsing "2026-08-31T13:49:35"
    // would read it as local time (CLAUDE.md §4.3).
    [Fact]
    public async Task GetById_SerializesTimestampsAsUtcWithAnExplicitZ()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);
        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>("/resources");
        var anyResource = page!.Items.First();

        var body = await client.GetFromJsonAsync<JsonElement>($"/resources/{anyResource.Id}");

        Assert.EndsWith("Z", body.GetProperty("createdAtUtc").GetString());
        Assert.EndsWith("Z", body.GetProperty("updatedAtUtc").GetString());
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFoundWithTheReasonCode()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/resources/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ResourceNotFound", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task GetById_EmptyGuid_IsAValidationFailureNotANotFound()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/resources/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- AC-4: cross-tenant reads ----

    // The body must match the unknown-id case above: a 404 that differed in any
    // way would confirm the id exists in another tenant.
    [Fact]
    public async Task GetById_WithAnotherTenantsRealId_ReturnsTheSameNotFound()
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/resources/{globexResourceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ResourceNotFound", body.GetProperty("reasonCode").GetString());
    }

    // Both tenants have identically-shaped resources, so a leaking filter would
    // show up as four rows rather than two — AC-4's exact scenario.
    [Fact]
    public async Task List_NeverIncludesAnotherTenantsResources()
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
            $"/resources?pageSize={PagingDefaults.MaxPageSize}&includeArchived=true");

        Assert.DoesNotContain(globexResourceId, page!.Items.Select(r => r.Id));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        var client = _host.CreateClient();

        var list = await client.GetAsync("/resources");
        var byId = await client.GetAsync($"/resources/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, byId.StatusCode);
    }

    // Not a bug: TenantMember requires the orgId claim, which decision 0012
    // deliberately omits for a SysAdmin (PRD §2).
    [Fact]
    public async Task SysAdmin_IsForbiddenFromTenantResourceReads()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.GetAsync("/resources");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Reads are TenantMember, not TenantAdmin — a member has to browse in order
    // to book. The admin gets the same reads.
    [Fact]
    public async Task TenantAdmin_CanReadToo()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>("/resources");

        Assert.Equal(2, page!.TotalCount);
    }

    // ---- FR-3.5: archived resources ----
    //
    // Nothing in the seed data is archived, so these three create their own row
    // and remove it again (WithArchivedAcmeResourceAsync). The host and its
    // database are shared across the whole collection, and other tests —
    // TenantIsolationTests among them — assert on Acme's exact resource count;
    // a test that permanently added a row would break them from a distance.

    [Fact]
    public Task List_ExcludesArchivedResourcesByDefault() =>
        WithArchivedAcmeResourceAsync("Retired Projector", async archivedId =>
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
                $"/resources?pageSize={PagingDefaults.MaxPageSize}");

            Assert.DoesNotContain(archivedId, page!.Items.Select(r => r.Id));
        });

    [Fact]
    public Task List_IncludesArchivedResourcesWhenAskedTo() =>
        WithArchivedAcmeResourceAsync("Retired Whiteboard", async archivedId =>
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var page = await client.GetFromJsonAsync<PagedResult<ResourceSummaryResponse>>(
                $"/resources?pageSize={PagingDefaults.MaxPageSize}&includeArchived=true");

            var archived = page!.Items.Single(r => r.Id == archivedId);
            Assert.True(archived.IsArchived);
        });

    // FR-3.5: archiving preserves the resource and its history, so it stays
    // directly readable — only the list hides it.
    [Fact]
    public Task GetById_StillReturnsAnArchivedResource() =>
        WithArchivedAcmeResourceAsync("Retired Scanner", async archivedId =>
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var detail = await client.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{archivedId}");

            Assert.Equal(archivedId, detail!.Id);
            Assert.True(detail.IsArchived);
        });

    // ---- Helpers ----

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

    // Test setup needs to see across tenants to know what real id to probe
    // with, independent of what the isolation layer under test allows a request
    // to see — same justification as TenantIsolationTests. TenantBypassScope
    // covers RLS, IgnoreQueryFilters covers the EF filter, and both are needed:
    // this scope has no HttpContext, so ICurrentTenant.OrgId is null.
    private async Task<Guid> GetAnyResourceIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources.IgnoreQueryFilters().FirstAsync(r => r.OrgId == orgId);
        return resource.Id;
    }

    // Creates an archived Acme resource, runs the assertions against it, and
    // removes it again whether or not they passed — the row must not outlive the
    // test (see the FR-3.5 section comment).
    //
    // A hard delete, which the application itself never does (CLAUDE.md §4.5):
    // this is fixture teardown, not a code path anyone can reach. The
    // alternative — an archived row left behind forever — is what §4.5 is
    // actually protecting, and it isn't this.
    private async Task WithArchivedAcmeResourceAsync(string name, Func<Guid, Task> assertions)
    {
        var archivedId = await CreateArchivedAcmeResourceAsync(name);

        try
        {
            await assertions(archivedId);
        }
        finally
        {
            await DeleteResourceAsync(archivedId);
        }
    }

    // Built through the domain constructor and Archive(), not raw SQL: unlike
    // Bookings (WP-3 decision D4), Resources has no stored-procedure write path
    // that CLAUDE.md §4.1 reserves, so the ordinary aggregate is the way in.
    private async Task<Guid> CreateArchivedAcmeResourceAsync(string name)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var acmeOrgId = await context.Organizations.Where(o => o.Slug == "acme").Select(o => o.Id).SingleAsync();
        var adminId = await context.Users.IgnoreQueryFilters()
            .Where(u => u.Email == AcmeAdmin).Select(u => u.Id).SingleAsync();

        var now = DateTime.UtcNow;
        var resource = new Resource(
            Guid.NewGuid(), acmeOrgId, name, "Equipment",
            capacity: 1, timeZoneId: "America/New_York", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: adminId, nowUtc: now);
        resource.Archive(adminId, now);

        context.Resources.Add(resource);
        await context.SaveChangesAsync();

        return resource.Id;
    }

    // IgnoreQueryFilters as well as the bypass scope: ExecuteDeleteAsync applies
    // the global query filter, which matches nothing here because this scope has
    // no tenant context.
    private async Task DeleteResourceAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        await context.Resources
            .IgnoreQueryFilters()
            .Where(r => r.Id == resourceId)
            .ExecuteDeleteAsync();
    }
}
