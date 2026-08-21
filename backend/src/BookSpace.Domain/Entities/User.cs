using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// FR-1.5 one tenant per user; OrgId null = SysAdmin, above all tenants.
// No self-registration: every user is provisioned by a SysAdmin or
// TenantAdmin, so CreatedByUserId is always required — the bootstrap
// SysAdmin row self-references its own Id (docs/bookspace-schema-v2.sql).
public class User : IAuditable, ITenantOwned
{
    private readonly List<RoleAssignment> _roleAssignments = new();

    public Guid Id { get; private set; }
    public Guid? OrgId { get; private set; }
    public string Email { get; private set; }
    public string PasswordHash { get; private set; }
    public string FullName { get; private set; }
    public bool IsActive { get; private set; }
    public Guid CalendarFeedToken { get; private set; }

    // UserRoles is a pure (UserId, Role) join with no surrogate Id. Backed by
    // RoleAssignment (below) rather than a raw List<Role> so EF Core's OwnsMany
    // can track it directly — SaveChanges then adds/removes UserRoles rows on
    // its own, no repository-level diff needed.
    public IReadOnlyCollection<Role> Roles => _roleAssignments.Select(r => r.Role).ToList().AsReadOnly();

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // EF Core materialization only — see Organization.cs for why this is needed.
    private User()
    {
        Email = string.Empty;
        PasswordHash = string.Empty;
        FullName = string.Empty;
    }

    public User(
        Guid id,
        Guid? orgId,
        string email,
        string passwordHash,
        string fullName,
        Guid createdByUserId,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("PasswordHash is required.", nameof(passwordHash));
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("FullName is required.", nameof(fullName));

        Id = id;
        OrgId = orgId;
        Email = email;
        PasswordHash = passwordHash;
        FullName = fullName;
        IsActive = true;
        CalendarFeedToken = Guid.NewGuid();
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

    public void AddRole(Role role, Guid actorUserId, DateTime nowUtc)
    {
        if (_roleAssignments.Any(r => r.Role == role))
            return;

        _roleAssignments.Add(new RoleAssignment(role));
        Touch(actorUserId, nowUtc);
    }

    public void RemoveRole(Role role, Guid actorUserId, DateTime nowUtc)
    {
        if (_roleAssignments.RemoveAll(r => r.Role == role) > 0)
            Touch(actorUserId, nowUtc);
    }

    public void Deactivate(Guid actorUserId, DateTime nowUtc)
    {
        IsActive = false;
        Touch(actorUserId, nowUtc);
    }

    public void Reactivate(Guid actorUserId, DateTime nowUtc)
    {
        IsActive = true;
        Touch(actorUserId, nowUtc);
    }

    private void Touch(Guid actorUserId, DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }

    // EF Core owned-collection backing for the UserRoles table (see
    // Infrastructure/Persistence/Configurations/UserConfiguration.cs) — public
    // only because OwnsMany's generic overload needs a type Infrastructure can
    // name; it carries no behavior of its own.
    public sealed class RoleAssignment
    {
        public Role Role { get; private set; }

        private RoleAssignment()
        {
        }

        public RoleAssignment(Role role)
        {
            Role = role;
        }
    }
}
