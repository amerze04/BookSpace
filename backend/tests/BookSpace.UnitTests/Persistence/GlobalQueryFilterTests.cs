using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.UnitTests.Persistence;

// CLAUDE.md §4.2, mechanism 1: exercises the LINQ query-filter expressions
// BookSpaceDbContext.OnModelCreating registers. EF InMemory, deliberately —
// this proves the filter shape/translation, not RLS or locking, so CLAUDE.md
// §8's "real SQL Server" rule doesn't apply (see TenantIsolationTests in
// BookSpace.IntegrationTests for the real-SQL-Server RLS coverage this
// complements, not replaces).
public class GlobalQueryFilterTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Resources_OnlyReturnsCurrentTenantsRows()
    {
        var acmeOrgId = Guid.NewGuid();
        var globexOrgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        await SeedAsync(databaseName, [NewResource(acmeOrgId, actorId), NewResource(globexOrgId, actorId)]);

        await using var acmeContext = CreateContext(databaseName, acmeOrgId);
        var visible = await acmeContext.Resources.ToListAsync();

        Assert.Single(visible);
        Assert.Equal(acmeOrgId, visible[0].OrgId);
    }

    [Fact]
    public async Task Resources_WithNoTenantContext_ReturnsNoRows()
    {
        var databaseName = Guid.NewGuid().ToString();
        await SeedAsync(databaseName, [NewResource(Guid.NewGuid(), Guid.NewGuid())]);

        await using var context = CreateContext(databaseName, currentTenantOrgId: null);
        var visible = await context.Resources.ToListAsync();

        Assert.Empty(visible);
    }

    [Fact]
    public async Task Users_WithNoTenantContext_ReturnsOnlySysAdminRows()
    {
        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        var sysAdmin = new User(actorId, orgId: null, "sysadmin@bookspace.local", "hash", "Sys Admin", actorId, Now);
        var tenantUser = new User(Guid.NewGuid(), orgId, "member@acme.test", "hash", "Member", actorId, Now);

        await using (var writer = CreateContext(databaseName, currentTenantOrgId: null))
        {
            writer.Users.AddRange(sysAdmin, tenantUser);
            await writer.SaveChangesAsync();
        }

        await using var context = CreateContext(databaseName, currentTenantOrgId: null);
        var visible = await context.Users.ToListAsync();

        Assert.Single(visible);
        Assert.Null(visible[0].OrgId);
    }

    [Fact]
    public async Task IgnoreQueryFilters_StillReturnsEveryTenantsRows()
    {
        var acmeOrgId = Guid.NewGuid();
        var globexOrgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        await SeedAsync(databaseName, [NewResource(acmeOrgId, actorId), NewResource(globexOrgId, actorId)]);

        await using var context = CreateContext(databaseName, acmeOrgId);
        var visible = await context.Resources.IgnoreQueryFilters().ToListAsync();

        Assert.Equal(2, visible.Count);
    }

    private static async Task SeedAsync(string databaseName, IEnumerable<Resource> resources)
    {
        await using var writer = CreateContext(databaseName, currentTenantOrgId: null);
        writer.Resources.AddRange(resources);
        await writer.SaveChangesAsync();
    }

    private static BookSpaceDbContext CreateContext(string databaseName, Guid? currentTenantOrgId)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new BookSpaceDbContext(options, new FixedCurrentTenant(currentTenantOrgId));
    }

    private static Resource NewResource(Guid orgId, Guid actorId) => new(
        Guid.NewGuid(), orgId, "Conference Room A", "Room",
        capacity: 4, timeZoneId: "UTC", requiresApproval: false,
        minDurationMinutes: null, maxDurationMinutes: null,
        description: null, createdByUserId: actorId, nowUtc: Now);
}
