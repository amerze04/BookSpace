using BookSpace.Domain.Entities;
using BookSpace.IntegrationTests.Support;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.IntegrationTests;

// Proves the decision-0010 schema change actually took effect: an email
// identifies exactly one user platform-wide, which is what lets login take
// email + password with no tenant discriminator.
//
// Real SQL Server, per CLAUDE.md §8 — a unique index is exactly the kind of
// behavior the in-memory provider does not have, so this test would pass there
// while the constraint was missing in production.
public class UserEmailUniquenessTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_EmailUniquenessTests");

    private BookSpaceDbContext _context = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        // No tenant context: SaveChanges* tenant-ownership validation
        // (CLAUDE.md §4.2) no-ops with a null current tenant, so inserting
        // users across two different orgs in one context — exactly what these
        // tests do — behaves the same as before Phase 4.
        _context = new BookSpaceDbContext(options, new FixedCurrentTenant(null));

        await _context.Database.EnsureDeletedAsync();
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // The behavior that changed: this used to be legal, because the old index was
    // (OrgId, Email).
    [Fact]
    public async Task SameEmailInTwoDifferentOrganizations_IsRejectedByTheDatabase()
    {
        var (orgOne, orgTwo, sysAdminId) = await SeedTwoOrganizationsAsync();
        var now = DateTime.UtcNow;

        _context.Users.Add(new User(
            Guid.NewGuid(), orgOne, "shared@example.test", "hash", "First", sysAdminId, now));
        await _context.SaveChangesAsync();

        _context.Users.Add(new User(
            Guid.NewGuid(), orgTwo, "shared@example.test", "hash", "Second", sysAdminId, now));

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    [Fact]
    public async Task SameEmailTwiceInOneOrganization_IsRejectedByTheDatabase()
    {
        var (orgOne, _, sysAdminId) = await SeedTwoOrganizationsAsync();
        var now = DateTime.UtcNow;

        _context.Users.Add(new User(
            Guid.NewGuid(), orgOne, "duplicate@example.test", "hash", "First", sysAdminId, now));
        await _context.SaveChangesAsync();

        _context.Users.Add(new User(
            Guid.NewGuid(), orgOne, "duplicate@example.test", "hash", "Second", sysAdminId, now));

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    // The gap the old filtered index left: SysAdmin rows (OrgId NULL) were
    // excluded from uniqueness entirely, so two could share an email.
    [Fact]
    public async Task SameEmailForTwoSysAdmins_IsNowRejectedToo()
    {
        var (_, _, sysAdminId) = await SeedTwoOrganizationsAsync();
        var now = DateTime.UtcNow;

        _context.Users.Add(new User(
            Guid.NewGuid(), orgId: null, "ops@example.test", "hash", "Ops One", sysAdminId, now));
        await _context.SaveChangesAsync();

        _context.Users.Add(new User(
            Guid.NewGuid(), orgId: null, "ops@example.test", "hash", "Ops Two", sysAdminId, now));

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    // Normalization happens in the domain constructor, so a differently-cased
    // duplicate collides rather than sneaking past.
    [Fact]
    public async Task SameEmailInDifferentCase_IsRejected()
    {
        var (orgOne, orgTwo, sysAdminId) = await SeedTwoOrganizationsAsync();
        var now = DateTime.UtcNow;

        _context.Users.Add(new User(
            Guid.NewGuid(), orgOne, "MixedCase@Example.test", "hash", "First", sysAdminId, now));
        await _context.SaveChangesAsync();

        _context.Users.Add(new User(
            Guid.NewGuid(), orgTwo, "mixedcase@example.test", "hash", "Second", sysAdminId, now));

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    [Fact]
    public void EmailIsStoredNormalized()
    {
        var id = Guid.NewGuid();

        var user = new User(id, null, "  MixedCase@Example.test  ", "hash", "Test", id, DateTime.UtcNow);

        Assert.Equal("mixedcase@example.test", user.Email);
    }

    // Two organizations plus the bootstrap SysAdmin the FK chain needs.
    private async Task<(Guid OrgOne, Guid OrgTwo, Guid SysAdminId)> SeedTwoOrganizationsAsync()
    {
        var now = DateTime.UtcNow;
        var sysAdminId = Guid.NewGuid();

        _context.Users.Add(new User(
            sysAdminId, null, "bootstrap@example.test", "hash", "Bootstrap", sysAdminId, now));

        var orgOne = new Organization(
            Guid.NewGuid(), "Org One", "org-one", "UTC", 60, 15, 24, sysAdminId, now);
        var orgTwo = new Organization(
            Guid.NewGuid(), "Org Two", "org-two", "UTC", 60, 15, 24, sysAdminId, now);

        _context.Organizations.AddRange(orgOne, orgTwo);
        await _context.SaveChangesAsync();

        return (orgOne.Id, orgTwo.Id, sysAdminId);
    }
}
