namespace BookSpace.Domain.Enums;

// FR-1.4 / FR-1.5: roles are additive within a tenant, not mutually exclusive.
public enum Role
{
    SysAdmin,
    TenantAdmin,
    Approver,
    Member
}
