namespace BookSpace.Domain.Common;

// CLAUDE.md §4.2: backs the global query filters on Users, Resources and
// Bookings. Nullable because Users.OrgId is null for a SysAdmin (above all
// tenants) — Resources and Bookings are always non-null in practice but
// implement this explicitly since their own OrgId property is a plain Guid.
public interface ITenantOwned
{
    Guid? OrgId { get; }
}
