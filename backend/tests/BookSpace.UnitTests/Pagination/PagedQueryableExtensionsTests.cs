using BookSpace.Application.Common.Pagination;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using BookSpace.UnitTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.UnitTests.Pagination;

// EF InMemory, same justification as GlobalQueryFilterTests: what's under test
// is Skip/Take/Count composition and the ordering guard, not SQL generation or
// locking, so CLAUDE.md §8's real-SQL-Server rule doesn't apply here.
public class PagedQueryableExtensionsTests
{
    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AcmeOrgId = Guid.NewGuid();
    private static readonly Guid GlobexOrgId = Guid.NewGuid();

    private sealed record ProbeQuery(int Page, int PageSize, string? Sort = null) : IPagedQuery;

    [Fact]
    public async Task ToPagedResultAsync_ReturnsTheRequestedSliceAndTheFullTotal()
    {
        await using var context = await SeededContextAsync(resourcesPerOrg: 7);

        var page = await context.Resources
            .OrderBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => r.Name)
            .ToPagedResultAsync(new ProbeQuery(Page: 1, PageSize: 3));

        Assert.Equal(["Room 01", "Room 02", "Room 03"], page.Items);
        Assert.Equal(7, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
    }

    [Fact]
    public async Task ToPagedResultAsync_LastPage_ReturnsTheRemainder()
    {
        await using var context = await SeededContextAsync(resourcesPerOrg: 7);

        var page = await context.Resources
            .OrderBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => r.Name)
            .ToPagedResultAsync(new ProbeQuery(Page: 3, PageSize: 3));

        Assert.Equal(["Room 07"], page.Items);
        Assert.False(page.HasNextPage);
        Assert.True(page.HasPreviousPage);
    }

    [Fact]
    public async Task ToPagedResultAsync_PageBeyondTheEnd_IsEmptyButKeepsTheTotal()
    {
        await using var context = await SeededContextAsync(resourcesPerOrg: 7);

        var page = await context.Resources
            .OrderBy(r => r.Id)
            .ToPagedResultAsync(new ProbeQuery(Page: 50, PageSize: 3));

        Assert.Empty(page.Items);
        Assert.Equal(7, page.TotalCount);
        Assert.False(page.HasNextPage);
    }

    // CLAUDE.md §4.2 plus the count query: both queries run through the same
    // DbContext, so the total can never advertise rows the page would hide.
    [Fact]
    public async Task ToPagedResultAsync_TotalCountRespectsTheTenantQueryFilter()
    {
        await using var context = await SeededContextAsync(resourcesPerOrg: 7);

        var page = await context.Resources
            .OrderBy(r => r.Id)
            .ToPagedResultAsync(new ProbeQuery(Page: 1, PageSize: 100));

        // 14 rows exist across two tenants; this context is scoped to Acme.
        Assert.Equal(7, page.TotalCount);
        Assert.Equal(7, page.Items.Count);
        Assert.All(page.Items, r => Assert.Equal(AcmeOrgId, r.OrgId));
    }

    // Offset paging over an unordered query silently returns undefined page
    // boundaries, so the guard fails loudly instead.
    [Fact]
    public async Task ToPagedResultAsync_UnorderedQuery_Throws()
    {
        await using var context = await SeededContextAsync(resourcesPerOrg: 3);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Resources.ToPagedResultAsync(new ProbeQuery(Page: 1, PageSize: 2)));

        Assert.Contains("ordered query", exception.Message);
    }

    [Fact]
    public async Task ToPagedResultAsync_OrderingAppliedBeforeAProjection_IsStillDetected()
    {
        await using var context = await SeededContextAsync(resourcesPerOrg: 3);

        var page = await context.Resources
            .OrderByDescending(r => r.Name)
            .Select(r => r.Name)
            .ToPagedResultAsync(new ProbeQuery(Page: 1, PageSize: 2));

        Assert.Equal(["Room 03", "Room 02"], page.Items);
    }

    private static async Task<BookSpaceDbContext> SeededContextAsync(int resourcesPerOrg)
    {
        var databaseName = Guid.NewGuid().ToString();
        var actorId = Guid.NewGuid();

        await using (var writer = CreateContext(databaseName, currentTenantOrgId: null))
        {
            foreach (var orgId in new[] { AcmeOrgId, GlobexOrgId })
            {
                for (var i = 1; i <= resourcesPerOrg; i++)
                {
                    writer.Resources.Add(new Resource(
                        Guid.NewGuid(), orgId, $"Room {i:00}", "Room",
                        capacity: 4, timeZoneId: "UTC", requiresApproval: false,
                        minDurationMinutes: null, maxDurationMinutes: null,
                        description: null, createdByUserId: actorId, nowUtc: Now));
                }
            }

            await writer.SaveChangesAsync();
        }

        return CreateContext(databaseName, AcmeOrgId);
    }

    private static BookSpaceDbContext CreateContext(string databaseName, Guid? currentTenantOrgId)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new BookSpaceDbContext(options, new FixedCurrentTenant(currentTenantOrgId));
    }
}
