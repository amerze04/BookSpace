using BookSpace.Application.Abstractions;
using BookSpace.Domain.Common;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

public class BookSpaceDbContext : DbContext
{
    private readonly ICurrentTenant _currentTenant;

    public BookSpaceDbContext(DbContextOptions<BookSpaceDbContext> options, ICurrentTenant currentTenant)
        : base(options)
    {
        _currentTenant = currentTenant;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<AvailabilityWindow> AvailabilityWindows => Set<AvailabilityWindow>();
    public DbSet<BlackoutPeriod> BlackoutPeriods => Set<BlackoutPeriod>();
    public DbSet<RecurrenceRule> RecurrenceRules => Set<RecurrenceRule>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BookSpaceDbContext).Assembly);

        // CLAUDE.md §4.2, mechanism 1: global query filters on Users, Resources,
        // Bookings. The lambda captures _currentTenant (the instance, not a
        // value) and EF re-evaluates it per query — the standard, documented
        // EF Core multi-tenancy pattern. Nullable-Guid equality gets EF's
        // null-safe translation for free, which gives the right fail-closed
        // behavior with no extra code: with no tenant context
        // (_currentTenant.OrgId == null), Resources/Bookings (OrgId NOT NULL)
        // match zero rows, and Users matches only other OrgId-IS-NULL rows
        // (SysAdmins) — never a real tenant's data.
        modelBuilder.Entity<User>().HasQueryFilter(u => u.OrgId == _currentTenant.OrgId);
        modelBuilder.Entity<Resource>().HasQueryFilter(r => r.OrgId == _currentTenant.OrgId);
        modelBuilder.Entity<Booking>().HasQueryFilter(b => b.OrgId == _currentTenant.OrgId);

        // CLAUDE.md §4.3: every instant is datetime2(0)/time(0) — set once here
        // instead of a HasPrecision(0) call on every DateTime/TimeOnly property
        // in every configuration.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?)
                    || property.ClrType == typeof(TimeOnly) || property.ClrType == typeof(TimeOnly?))
                {
                    property.SetPrecision(0);
                }
            }
        }

        base.OnModelCreating(modelBuilder);
    }

    // CLAUDE.md §4.2, mechanism 2. ITenantOwned.OrgId is get-only and every
    // implementing entity already requires OrgId at construction (Booking's is
    // even DB-constrained to match its Resource's via
    // FK_Bookings_Resources_SameOrg), so there is no setter to "set" here —
    // this is a validation guard, not an assignment. It catches an
    // application-layer bug (the wrong OrgId reached construction, or an
    // entity read via a bypass path got attached under the wrong tenant's
    // scope) before it reaches disk. Covers both Added and Modified: no
    // current domain method changes OrgId after construction, but the check
    // costs nothing extra and closes the door on a future one doing so by
    // accident.
    public override int SaveChanges()
    {
        ValidateTenantOwnership();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTenantOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ValidateTenantOwnership();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateTenantOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateTenantOwnership()
    {
        var currentOrgId = _currentTenant.OrgId;
        if (currentOrgId is null)
        {
            // No tenant context: SeedData, or a future SysAdmin-driven
            // provisioning flow creating a tenant's first user. Nothing to
            // validate against — refusing here would break seeding.
            return;
        }

        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Entity is not ITenantOwned owned)
            {
                continue;
            }

            if (owned.OrgId != currentOrgId)
            {
                throw new TenantIsolationViolationException(entry.Entity.GetType().Name, owned.OrgId, currentOrgId.Value);
            }
        }
    }
}
