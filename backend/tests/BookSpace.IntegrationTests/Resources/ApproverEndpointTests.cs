using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ReplaceApprovers;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// WP-3 Phase 3 step 2, FR-3.3: PUT /resources/{id}/approvers through the real
// pipeline.
//
// The eligibility *rule* is unit-tested against a fake that simply declares who
// is eligible. What only this file can prove is the rule's three real conditions,
// each of which needs a database and a tenant: the role test against actual
// UserRoles rows, the IsActive column, and — the one that matters most —
// cross-tenant refusal coming from CLAUDE.md §4.2's query filter and RLS rather
// than from a predicate anybody wrote.
//
// Same state hygiene as the sibling files: everything created is removed again,
// and the one test that deactivates a seeded user reactivates it in a finally,
// because the host and its database are shared across the collection.
[Collection(nameof(AuthenticationTestCollection))]
public class ApproverEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string GlobexApprover = "approver@globex.test";

    public ApproverEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static object ValidResource(string name, bool requiresApproval = false) =>
        new
        {
            name,
            description = "Created by the approver tests",
            resourceType = "Room",
            capacity = 4,
            timeZoneId = "America/New_York",
            requiresApproval,
            minDurationMinutes = 30,
            maxDurationMinutes = 240,
        };

    // ---- The happy path ----

    [Fact]
    public async Task Replace_WithAnApproverRoleUser_AssignsAndReturnsTheirName()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Happy Path");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);

            Assert.Equal(resource.Id, body!.ResourceId);
            var approver = Assert.Single(body.Approvers);
            Assert.Equal(approverId, approver.UserId);
            // The point of returning a body at all: a Guid is not something an
            // admin can check by eye.
            Assert.Equal("Resource Approver", approver.FullName);

            Assert.Equal(1, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // A TenantAdmin is eligible too, mirroring AuthorizationPolicies.Approver, so
    // the set that may be assigned and the set that may actually approve are the
    // same one. Owner's call, 2026-09-01.
    [Fact]
    public async Task Replace_WithATenantAdmin_IsAccepted()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Admin Assigned");
        var adminId = await UserIdAsync(AcmeAdmin);

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { adminId } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);
            Assert.Equal(adminId, Assert.Single(body!.Approvers).UserId);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Replace_ThenRead_ShowsTheApproversOnTheResourceDetail()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, "Approvers On The Detail");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            var before = await adminClient.GetFromJsonAsync<GetResourceQueryResponse>(
                $"/resources/{resource.Id}", TestJson.Options);
            Assert.Empty(before!.Approvers);

            await adminClient.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });

            // Read as a *member*: someone deciding whether to book a room that
            // needs approval should be able to see who will be deciding.
            var memberClient = await AuthenticatedClientAsync(AcmeMember);
            var detail = await memberClient.GetFromJsonAsync<GetResourceQueryResponse>(
                $"/resources/{resource.Id}", TestJson.Options);

            var approver = Assert.Single(detail!.Approvers);
            Assert.Equal(approverId, approver.UserId);
            Assert.Equal("Resource Approver", approver.FullName);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Replace_DiscardsThePreviousApprovers()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Replaced");
        var approverId = await UserIdAsync(AcmeApprover);
        var adminId = await UserIdAsync(AcmeAdmin);

        try
        {
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId, adminId } });

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { adminId } });

            var body = await response.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);

            Assert.Equal(adminId, Assert.Single(body!.Approvers).UserId);

            // One row in ResourceApprovers, not three.
            Assert.Equal(1, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Replace_IsIdempotent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Sent Twice");
        var approverId = await UserIdAsync(AcmeApprover);
        var payload = new { approverUserIds = new[] { approverId } };

        try
        {
            var first = await client.PutAsJsonAsync($"/resources/{resource.Id}/approvers", payload);
            var second = await client.PutAsJsonAsync($"/resources/{resource.Id}/approvers", payload);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var firstBody = await first.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);
            var secondBody = await second.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);

            // Identical, unlike the schedule endpoint: an approver row is keyed by
            // (ResourceId, UserId), so there is no server-minted id to churn
            // between calls. Compared field by field rather than with record
            // equality — a positional record holding a list compares that list by
            // reference, so two structurally identical responses are never equal.
            Assert.Equal(firstBody!.ResourceId, secondBody!.ResourceId);
            Assert.Equal(firstBody.RequiresApproval, secondBody.RequiresApproval);
            Assert.Equal(firstBody.Approvers, secondBody.Approvers);
            Assert.Equal(1, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // ---- Eligibility: the three conditions, each against real data ----

    // A Member holds neither Approver nor TenantAdmin.
    [Fact]
    public async Task Replace_WithAMember_Returns422ApproverNotEligible()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Member Refused");
        var memberId = await UserIdAsync(AcmeMember);

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { memberId } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ApproverNotEligible");
            Assert.Equal(0, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // AC-4, and the reason this whole endpoint needed a tenant-filtered user read.
    // Globex's approver is a real, active, Approver-role user — and must still be
    // refused, with a body that says nothing about which of the three conditions
    // failed.
    [Fact]
    public async Task Replace_WithAnotherTenantsRealApprover_Returns422ApproverNotEligible()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Cross Tenant");
        var globexApproverId = await UserIdAsync(GlobexApprover);

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { globexApproverId } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ApproverNotEligible");
            Assert.Equal(0, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // A cross-tenant id must be indistinguishable from a nonexistent one, not
    // merely refused — otherwise the response confirms the id exists somewhere,
    // which is what ApproverNotEligible was named to avoid
    // (docs/decisions/0016).
    [Fact]
    public async Task Replace_CrossTenantApprover_IsIndistinguishableFromAnUnknownId()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Indistinguishable");
        var globexApproverId = await UserIdAsync(GlobexApprover);

        try
        {
            var crossTenant = await ReadProblemWithoutPerRequestFieldsAsync(
                await client.PutAsJsonAsync(
                    $"/resources/{resource.Id}/approvers",
                    new { approverUserIds = new[] { globexApproverId } }));

            var unknown = await ReadProblemWithoutPerRequestFieldsAsync(
                await client.PutAsJsonAsync(
                    $"/resources/{resource.Id}/approvers",
                    new { approverUserIds = new[] { Guid.NewGuid() } }));

            Assert.Equal(unknown, crossTenant);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // A deactivated approver would silently stall every approval routed to them,
    // so IsActive is part of eligibility. Owner's call, 2026-09-01.
    [Fact]
    public async Task Replace_WithADeactivatedApprover_Returns422ApproverNotEligible()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Deactivated");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            await SetUserActiveAsync(approverId, isActive: false);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ApproverNotEligible");
        }
        finally
        {
            // Reactivated in a finally, or every later test in the collection that
            // needs an Approver fails — and only in a full run. Exactly the trap
            // AuthenticationEndpointTests left behind before Phase 2 fixed it.
            await SetUserActiveAsync(approverId, isActive: true);
            await DeleteResourceAsync(resource.Id);
        }
    }

    // ---- The RequiresApproval invariant, from the approver side ----

    // Phase 2 blocked RequiresApproval = true with an empty list on create and
    // edit. Without the same check here the state FR-3.3 rules out comes back
    // through the side door.
    [Fact]
    public async Task Replace_EmptyList_OnAResourceRequiringApproval_Returns422ApproversRequired()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Cannot Be Emptied");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            // Assign an approver, then set the flag — the only order that works,
            // since Phase 2 refuses the flag on a resource with no approvers.
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });
            var flagged = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}",
                ValidResource("Approvers Cannot Be Emptied", requiresApproval: true));
            flagged.EnsureSuccessStatusCode();

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = Array.Empty<Guid>() });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ApproversRequired");

            // The approver survived: rule checks run before any mutator.
            Assert.Equal(1, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // The whole reason approvers use replace-the-set: swapping one for another is
    // a single request with no invalid state in between. Per-row POST/DELETE would
    // have to pass through the empty list, which the test above shows is refused.
    [Fact]
    public async Task Replace_CanSwapApproversOnAResourceRequiringApproval()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Swapped");
        var approverId = await UserIdAsync(AcmeApprover);
        var adminId = await UserIdAsync(AcmeAdmin);

        try
        {
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}",
                ValidResource("Approvers Swapped", requiresApproval: true));

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { adminId } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);
            Assert.True(body!.RequiresApproval);
            Assert.Equal(adminId, Assert.Single(body.Approvers).UserId);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // An empty list is fine when nothing requires approval — the only way to take
    // a resource back out of the approval flow.
    [Fact]
    public async Task Replace_EmptyList_OnAResourceNotRequiringApproval_ClearsTheList()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Cleared");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = Array.Empty<Guid>() });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ReplaceApproversCommandResponse>(
                TestJson.Options);
            Assert.Empty(body!.Approvers);
            Assert.Equal(0, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // FR-3.3 end to end, and the gap Phase 2 documented: until this endpoint
    // existed, only a resource that somehow already had an approver could carry
    // the flag. An admin can now publish an approval-gated resource in two calls.
    [Fact]
    public async Task AnAdminCanPublishAResourceThatRequiresApproval()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approval Gated Room");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            var assigned = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });
            assigned.EnsureSuccessStatusCode();

            var flagged = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}",
                ValidResource("Approval Gated Room", requiresApproval: true));
            flagged.EnsureSuccessStatusCode();

            var detail = await client.GetFromJsonAsync<GetResourceQueryResponse>(
                $"/resources/{resource.Id}", TestJson.Options);

            Assert.True(detail!.RequiresApproval);
            Assert.Equal(approverId, Assert.Single(detail.Approvers).UserId);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // ---- Shape and authorization ----

    [Fact]
    public async Task Replace_DuplicateIds_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers Duplicated");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId, approverId } });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertReasonCodeAsync(response, "ValidationFailed");
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Replace_OnAnArchivedResource_Returns422ResourceArchived()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Approvers On Archived");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            await client.PostAsync($"/resources/{resource.Id}/archive", content: null);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Replace_UnknownResource_Returns404ResourceNotFound()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var approverId = await UserIdAsync(AcmeApprover);

        var response = await client.PutAsJsonAsync(
            $"/resources/{Guid.NewGuid()}/approvers",
            new { approverUserIds = new[] { approverId } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    // The resource id is checked before the approver ids, so another tenant's
    // room is a 404 and never reaches the eligibility query.
    [Fact]
    public async Task Replace_AnotherTenantsRealResource_Returns404AndWritesNothing()
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);
        var acmeApproverId = await UserIdAsync(AcmeApprover);
        var before = await StoredApproverCountAsync(globexResourceId);

        var response = await acmeClient.PutAsJsonAsync(
            $"/resources/{globexResourceId}/approvers",
            new { approverUserIds = new[] { acmeApproverId } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
        Assert.Equal(before, await StoredApproverCountAsync(globexResourceId));
    }

    // WP-3's AC: "non-admins cannot create or edit resources". The Approver role
    // specifically, because being an approver is not the same as choosing them.
    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    public async Task Replace_AsANonAdmin_IsForbidden(string email)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, $"Approvers Guarded {email}");
        var approverId = await UserIdAsync(AcmeApprover);

        try
        {
            var client = await AuthenticatedClientAsync(email);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/approvers",
                new { approverUserIds = new[] { approverId } });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await StoredApproverCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // ---- Helpers ----

    private async Task<CreateResourceCommandResponse> CreateResourceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/resources", ValidResource(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;
    }

    // Both isolation layers bypassed, same justification as TenantIsolationTests:
    // setup has to see across tenants to know what real id to probe with.
    private async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Users
            .IgnoreQueryFilters()
            .Where(u => u.Email == email)
            .Select(u => u.Id)
            .SingleAsync();
    }

    private async Task SetUserActiveAsync(Guid userId, bool isActive)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        await context.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, isActive));
    }

    // Counted against the physical table with both isolation layers bypassed, so
    // "nothing was written" means nothing was written — not "the API declines to
    // show it".
    //
    // ResourceApprovers is an EF owned collection over a private field, so it
    // cannot be queried through a DbSet; loading the resource brings it along.
    private async Task<int> StoredApproverCountAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == resourceId);

        return resource?.ApproverUserIds.Count ?? 0;
    }

    private async Task<Guid> GetAnyResourceIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources.IgnoreQueryFilters().FirstAsync(r => r.OrgId == orgId);
        return resource.Id;
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

    private static async Task AssertReasonCodeAsync(HttpResponseMessage response, string expected)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, body.GetProperty("reasonCode").GetString());
    }

    // Strips the two fields that legitimately differ per request, so what is left
    // is the part two refusals must share exactly.
    private static async Task<string> ReadProblemWithoutPerRequestFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        body!.Remove("traceId");
        body.Remove("correlationId");

        return string.Join(
            "|",
            body.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.ToString()}"));
    }

    // Fixture teardown, which the application never does (CLAUDE.md §4.5 archives
    // instead). ResourceApprovers goes with the resource — its FK is the one
    // documented EF-forced cascade (see ResourceConfiguration).
    private async Task DeleteResourceAsync(Guid resourceId)
    {
        if (resourceId == Guid.Empty)
        {
            return;
        }

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        await context.Resources
            .IgnoreQueryFilters()
            .Where(r => r.Id == resourceId)
            .ExecuteDeleteAsync();
    }
}
