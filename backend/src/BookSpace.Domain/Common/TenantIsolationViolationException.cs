namespace BookSpace.Domain.Common;

// CLAUDE.md §4.2: thrown by BookSpaceDbContext.SaveChanges* when an
// ITenantOwned entity being added or modified doesn't belong to the current
// tenant. This indicates an application-layer bug (the caller computed the
// wrong OrgId, or attached an entity read via a SysAdmin/bypass path into the
// wrong tenant's scope) — it is not something a client should ever trigger
// through normal use, so GlobalExceptionHandler maps it to a generic 500
// rather than surfacing any tenant detail to the response.
public sealed class TenantIsolationViolationException : Exception
{
    public TenantIsolationViolationException(string entityTypeName, Guid? entityOrgId, Guid currentTenantOrgId)
        : base($"Refusing to save {entityTypeName} with OrgId '{entityOrgId}' — current tenant is '{currentTenantOrgId}'.")
    {
        EntityTypeName = entityTypeName;
        EntityOrgId = entityOrgId;
        CurrentTenantOrgId = currentTenantOrgId;
    }

    public string EntityTypeName { get; }
    public Guid? EntityOrgId { get; }
    public Guid CurrentTenantOrgId { get; }
}
