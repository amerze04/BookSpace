using BookSpace.Domain.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
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

    // WP-3 decision D1: AvailabilityWindows and BlackoutPeriods became
    // ITenantOwned in this step, so mechanism 2 now covers them as well.
    [Fact]
    public async Task SaveChanges_AddedAvailabilityWindowMatchingCurrentTenant_Succeeds()
    {
        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using var context = CreateContext(Guid.NewGuid().ToString(), orgId);

        var resource = NewResource(orgId, actorId);
        resource.AddWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), actorId, Now);
        context.Resources.Add(resource);

        await context.SaveChangesAsync();

        Assert.Equal(1, await context.AvailabilityWindows.CountAsync());
    }

    // A window's OrgId can only come from its resource
    // (Resource.ReplaceAvailabilityWindows is its only creator), so the way it goes
    // wrong is a resource belonging to another tenant. Only the window is
    // tracked here — it has no navigation back to Resource — which keeps the
    // entity the guard reports deterministic.
    [Fact]
    public async Task SaveChanges_AddedAvailabilityWindowUnderAnotherTenant_Throws()
    {
        var currentOrgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using var context = CreateContext(Guid.NewGuid().ToString(), currentOrgId);

        var foreignResource = NewResource(otherOrgId, actorId);
        var window = foreignResource.AddWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), actorId, Now);
        context.AvailabilityWindows.Add(window);

        var exception = await Assert.ThrowsAsync<TenantIsolationViolationException>(() => context.SaveChangesAsync());
        Assert.Equal(otherOrgId, exception.EntityOrgId);
        Assert.Equal(currentOrgId, exception.CurrentTenantOrgId);
    }

    [Fact]
    public async Task SaveChanges_AddedBlackoutPeriodUnderAnotherTenant_Throws()
    {
        var currentOrgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using var context = CreateContext(Guid.NewGuid().ToString(), currentOrgId);

        context.BlackoutPeriods.Add(new BlackoutPeriod(
            Guid.NewGuid(), otherOrgId, Guid.NewGuid(),
            Now.AddDays(1), Now.AddDays(2), "Maintenance", actorId, Now));

        var exception = await Assert.ThrowsAsync<TenantIsolationViolationException>(() => context.SaveChangesAsync());
        Assert.Equal(otherOrgId, exception.EntityOrgId);
        Assert.Equal(currentOrgId, exception.CurrentTenantOrgId);
    }

    private static BookSpaceDbContext CreateContext(string databaseName, Guid? currentTenantOrgId)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new BookSpaceDbContext(options, new FixedCurrentTenant(currentTenantOrgId));
    }

    private static Resource NewResource(Guid orgId, Guid actorId) => new(
        Guid.NewGuid(), orgId, "Conference Room A", ResourceType.Room,
        capacity: 4, timeZoneId: "UTC", requiresApproval: false,
        minDurationMinutes: null, maxDurationMinutes: null,
        description: null, createdByUserId: actorId, nowUtc: Now);
}
