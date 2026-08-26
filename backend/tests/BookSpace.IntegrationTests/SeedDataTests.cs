using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.IntegrationTests;

// Needs a real local SQL Server instance — consistent with CLAUDE.md §8's
// rule that anything with real database behavior (locking, RLS, and here,
// actual constraint enforcement) must use a real SQL Server, not the
// in-memory provider. Runs against its own throwaway database so it never
// touches the "BookSpace" dev database that the app itself seeds.
public class SeedDataTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_SeedDataTests;Trusted_Connection=True;TrustServerCertificate=True;";

    // Real hasher: the seed now stores genuine PBKDF2 hashes, and asserting on
    // that is part of proving FR-2.3 holds for seeded accounts too.
    private static readonly IPasswordHasher PasswordHasher = new PasswordHasherAdapter();

    private BookSpaceDbContext _context = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        _context = new BookSpaceDbContext(options);

        await _context.Database.EnsureDeletedAsync();
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task SeedAsync_PopulatesExpectedRowCounts()
    {
        await SeedData.SeedAsync(_context, PasswordHasher);

        Assert.Equal(2, await _context.Organizations.CountAsync());

        var users = await _context.Users.ToListAsync();
        Assert.Equal(9, users.Count);
        Assert.Equal(9, users.Sum(u => u.Roles.Count)); // owned collections load with the owner

        Assert.Equal(4, await _context.Resources.CountAsync());
        Assert.Equal(20, await _context.AvailabilityWindows.CountAsync());
        Assert.Equal(2, await _context.BlackoutPeriods.CountAsync());
        Assert.Equal(2, await _context.RecurrenceRules.CountAsync());
        Assert.Equal(0, await _context.Bookings.CountAsync());
    }

    // FR-2.3: the seed stores real hashes now, not the WP-1 placeholder — so the
    // accounts are actually usable for login and no plaintext is on disk.
    [Fact]
    public async Task SeedAsync_StoresVerifiablePasswordHashesNotPlaintext()
    {
        await SeedData.SeedAsync(_context, PasswordHasher);

        var users = await _context.Users.ToListAsync();

        Assert.All(users, u =>
        {
            Assert.DoesNotContain(SeedData.SeedPassword, u.PasswordHash);
            Assert.True(PasswordHasher.Verify(u.PasswordHash, SeedData.SeedPassword));
        });
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent_WhenCalledTwice()
    {
        await SeedData.SeedAsync(_context, PasswordHasher);
        await SeedData.SeedAsync(_context, PasswordHasher);

        Assert.Equal(2, await _context.Organizations.CountAsync());
        Assert.Equal(9, await _context.Users.CountAsync());
    }

    [Fact]
    public async Task SeedAsync_BootstrapSysAdmin_SelfReferencesCreatedBy()
    {
        await SeedData.SeedAsync(_context, PasswordHasher);

        var sysAdmin = await _context.Users.SingleAsync(u => u.OrgId == null);

        Assert.Equal(sysAdmin.Id, sysAdmin.CreatedByUserId);
    }

    [Fact]
    public async Task SeedAsync_EachOrganizationHasATenantAdmin()
    {
        await SeedData.SeedAsync(_context, PasswordHasher);

        var orgIds = await _context.Organizations.Select(o => o.Id).ToListAsync();

        foreach (var orgId in orgIds)
        {
            var usersInOrg = await _context.Users.Where(u => u.OrgId == orgId).ToListAsync();
            Assert.Equal(4, usersInOrg.Count);
        }
    }
}
