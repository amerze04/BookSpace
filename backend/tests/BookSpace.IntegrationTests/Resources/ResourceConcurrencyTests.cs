using System.Net.Http.Headers;
using System.Net.Http.Json;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// Hardening pass, item 2: FR-3.3's invariant (RequiresApproval => at least one
// approver) is split across two endpoints, UpdateResource and ReplaceApprovers,
// each of which reads the resource, checks the invariant against what it just
// read, and saves — independently of the other. Before Resources carried a
// RowVersion, nothing stopped both from committing against the other's
// now-stale read: an admin sets RequiresApproval = true while the resource
// still has its one approver (legal at read time), a concurrent PUT clears the
// approver list while RequiresApproval was still false (also legal at read
// time), and both saves succeed, landing on exactly the state FR-3.3 forbids.
//
// Deterministic rather than timing-based, per CLAUDE.md §8's own preference:
// two DbContexts load the same row, stage their changes, and save in the order
// that actually produces the race, rather than firing concurrent HTTP requests
// and hoping they interleave.
[Collection(nameof(AuthenticationTestCollection))]
public class ResourceConcurrencyTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";

    public ResourceConcurrencyTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task SettingRequiresApprovalRacingAgainstClearingApprovers_TheSecondSaveIsRejected()
    {
        var resourceId = await CreateResourceWithOneApproverAsync();

        try
        {
            await using var scopeA = _host.CreateScope();
            var contextA = scopeA.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            await using var scopeB = _host.CreateScope();
            var contextB = scopeB.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

            using var _ = TenantBypassScope.Enter();

            // Both "requests" load the same row before either writes — the
            // shape of UpdateResourceCommandRequestHandler and
            // ReplaceApproversCommandRequestHandler each reading their own
            // tracked copy via FindForUpdateAsync.
            var resourceViaA = await contextA.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId);
            var resourceViaB = await contextB.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId);

            // Request A: an admin turns approval-gating on. Legal at the
            // instant it reads — the resource still has its one approver.
            resourceViaA.SetRequiresApproval(true, resourceViaA.CreatedByUserId, DateTime.UtcNow);

            // Request B: a concurrent PUT clears the approver list. Also legal
            // at the instant it reads — RequiresApproval was still false.
            resourceViaB.ReplaceApprovers(Array.Empty<Guid>(), resourceViaB.CreatedByUserId, DateTime.UtcNow);

            // B commits first...
            await contextB.SaveChangesAsync();

            // ...so A's RowVersion is now stale. Letting A's save through would
            // leave RequiresApproval = true with zero approvers — exactly what
            // FR-3.3 forbids — so it must be rejected instead.
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextA.SaveChangesAsync());

            // The persisted state is B's alone: approvers cleared, and
            // RequiresApproval never actually flipped, since A never committed.
            var final = await ReadResourceAsync(resourceId);
            Assert.False(final.RequiresApproval);
            Assert.Empty(final.ApproverUserIds);
        }
        finally
        {
            await DeleteResourceAsync(resourceId);
        }
    }

    // The other ordering, so the test does not merely prove "context B always
    // wins" — the invariant has to hold whichever side commits first.
    [Fact]
    public async Task ClearingApproversRacingAgainstSettingRequiresApproval_TheSecondSaveIsRejected()
    {
        var resourceId = await CreateResourceWithOneApproverAsync();

        try
        {
            await using var scopeA = _host.CreateScope();
            var contextA = scopeA.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            await using var scopeB = _host.CreateScope();
            var contextB = scopeB.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

            using var _ = TenantBypassScope.Enter();

            var resourceViaA = await contextA.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId);
            var resourceViaB = await contextB.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId);

            resourceViaA.SetRequiresApproval(true, resourceViaA.CreatedByUserId, DateTime.UtcNow);
            resourceViaB.ReplaceApprovers(Array.Empty<Guid>(), resourceViaB.CreatedByUserId, DateTime.UtcNow);

            // A commits first this time.
            await contextA.SaveChangesAsync();

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

            // The persisted state is A's alone: RequiresApproval is true, and
            // the one approver assigned at setup is still there.
            var final = await ReadResourceAsync(resourceId);
            Assert.True(final.RequiresApproval);
            Assert.Single(final.ApproverUserIds);
        }
        finally
        {
            await DeleteResourceAsync(resourceId);
        }
    }

    private async Task<Guid> CreateResourceWithOneApproverAsync()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var createResponse = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = "Resource Concurrency Test Room",
                description = "Created by ResourceConcurrencyTests",
                resourceType = "Room",
                capacity = 4,
                timeZoneId = "America/New_York",
                requiresApproval = false,
                minDurationMinutes = 30,
                maxDurationMinutes = 240,
            });
        createResponse.EnsureSuccessStatusCode();
        var created = (await createResponse.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(
            TestJson.Options))!;

        var approverId = await UserIdAsync(AcmeApprover);
        var approversResponse = await client.PutAsJsonAsync(
            $"/resources/{created.Id}/approvers",
            new { approverUserIds = new[] { approverId } });
        approversResponse.EnsureSuccessStatusCode();

        return created.Id;
    }

    private async Task<BookSpace.Domain.Entities.Resource> ReadResourceAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Resources.AsNoTracking().IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId);
    }

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

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = SeedData.SeedPassword });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // Fixture teardown, which the application never does (CLAUDE.md §4.5
    // archives instead).
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
