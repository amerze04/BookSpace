using BookSpace.Domain.Common;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.UnitTests.Persistence;

// CLAUDE.md §4.2, mechanism 2: exercises BookSpaceDbContext's SaveChanges*
// validation directly. EF InMemory, deliberately — this checks ChangeTracker
// inspection and LINQ-independent logic, not constraints or locking, so
// CLAUDE.md §8's "real SQL Server" rule doesn't apply here (see
// TenantIsolationTests in BookSpace.IntegrationTests for the real-SQL-Server,
// real-RLS coverage).
public class TenantOwnershipValidationTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SaveChanges_AddedEntityMatchingCurrentTenant_Succeeds()
    {
        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using var context = CreateContext(Guid.NewGuid().ToString(), orgId);

        context.Resources.Add(NewResource(orgId, actorId));

        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Resources.CountAsync());
    }

    [Fact]
    public async Task SaveChanges_AddedEntityUnderAnotherTenant_Throws()
    {
        var currentOrgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using var context = CreateContext(Guid.NewGuid().ToString(), currentOrgId);

        context.Resources.Add(NewResource(otherOrgId, actorId));

        var exception = await Assert.ThrowsAsync<TenantIsolationViolationException>(() => context.SaveChangesAsync());
        Assert.Equal(otherOrgId, exception.EntityOrgId);
        Assert.Equal(currentOrgId, exception.CurrentTenantOrgId);
    }

    [Fact]
    public async Task SaveChanges_NoCurrentTenant_SkipsValidationRegardlessOfEntityOrgId()
    {
        var actorId = Guid.NewGuid();
        await using var context = CreateContext(Guid.NewGuid().ToString(), currentTenantOrgId: null);

        context.Resources.Add(NewResource(Guid.NewGuid(), actorId));
        context.Resources.Add(NewResource(Guid.NewGuid(), actorId));

        await context.SaveChangesAsync();

        // IgnoreQueryFilters: with no current tenant, the query filter itself
        // (mechanism 1) would also hide these rows — this assertion is about
        // SaveChanges* validation (mechanism 2) not having blocked the
        // insert, not about what a filtered read would show.
        Assert.Equal(2, await context.Resources.IgnoreQueryFilters().CountAsync());
    }

    // Modified, not just Added: an entity created under tenant A that somehow
    // gets attached and modified under tenant B's context — e.g. loaded via a
    // future bypass path and saved through the wrong scope — must be caught
    // too. No current domain method reassigns OrgId itself; this proves the
    // guard against the entry's State, not against OrgId literally changing.
    [Fact]
    public async Task SaveChanges_ModifiedEntityUnderAnotherTenant_Throws()
    {
        var ownerOrgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        var resource = NewResource(ownerOrgId, actorId);

        await using (var writerContext = CreateContext(databaseName, ownerOrgId))
        {
            writerContext.Resources.Add(resource);
            await writerContext.SaveChangesAsync();
        }

        var otherOrgId = Guid.NewGuid();
        await using var readerContext = CreateContext(databaseName, otherOrgId);
        var attached = await readerContext.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resource.Id);
        attached.Archive(actorId, Now);

        await Assert.ThrowsAsync<TenantIsolationViolationException>(() => readerContext.SaveChangesAsync());
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
