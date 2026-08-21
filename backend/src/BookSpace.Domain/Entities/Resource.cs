using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

// FR-3.1 type/capacity/timezone; FR-3.5 archive preserves history.
// Capacity = concurrent units the resource supports, not seats within one
// exclusive booking — see docs/decisions/0005-capacity-semantics.md.
public class Resource : IAuditable, ITenantOwned
{
    private readonly List<ApproverAssignment> _approverAssignments = new();
    private readonly List<AvailabilityWindow> _availabilityWindows = new();

    public Guid Id { get; private set; }
    public Guid OrgId { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public string ResourceType { get; private set; }
    public int Capacity { get; private set; }
    public string TimeZoneId { get; private set; }
    public bool RequiresApproval { get; private set; }
    public int? MinDurationMinutes { get; private set; }
    public int? MaxDurationMinutes { get; private set; }
    public bool IsArchived { get; private set; }

    // FR-3.3: one or more approvers per resource. ResourceApprovers is a pure
    // (ResourceId, UserId) join with no surrogate Id. Backed by
    // ApproverAssignment (below) rather than a raw List<Guid> so EF Core's
    // OwnsMany can track it directly — see User.Roles for the same reasoning.
    public IReadOnlyCollection<Guid> ApproverUserIds => _approverAssignments.Select(a => a.UserId).ToList().AsReadOnly();
    public IReadOnlyCollection<AvailabilityWindow> AvailabilityWindows => _availabilityWindows.AsReadOnly();

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // EF Core materialization only — see Organization.cs for why this is needed.
    private Resource()
    {
        Name = string.Empty;
        ResourceType = string.Empty;
        TimeZoneId = string.Empty;
    }

    public Resource(
        Guid id,
        Guid orgId,
        string name,
        string resourceType,
        int capacity,
        string timeZoneId,
        bool requiresApproval,
        int? minDurationMinutes,
        int? maxDurationMinutes,
        string? description,
        Guid createdByUserId,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(resourceType))
            throw new ArgumentException("ResourceType is required.", nameof(resourceType));
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("TimeZoneId is required.", nameof(timeZoneId));
        if (capacity <= 0) // CK_Resources_Capacity
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

        Id = id;
        OrgId = orgId;
        Name = name;
        Description = description;
        ResourceType = resourceType;
        Capacity = capacity;
        TimeZoneId = timeZoneId;
        RequiresApproval = requiresApproval;
        MinDurationMinutes = minDurationMinutes;
        MaxDurationMinutes = maxDurationMinutes;
        IsArchived = false;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

    Guid? ITenantOwned.OrgId => OrgId;

    public void AddApprover(Guid userId, Guid actorUserId, DateTime nowUtc)
    {
        if (_approverAssignments.Any(a => a.UserId == userId))
            return;

        _approverAssignments.Add(new ApproverAssignment(userId));
        Touch(actorUserId, nowUtc);
    }

    public void RemoveApprover(Guid userId, Guid actorUserId, DateTime nowUtc)
    {
        if (_approverAssignments.RemoveAll(a => a.UserId == userId) > 0)
            Touch(actorUserId, nowUtc);
    }

    public void AddAvailabilityWindow(AvailabilityWindow window, Guid actorUserId, DateTime nowUtc)
    {
        _availabilityWindows.Add(window);
        Touch(actorUserId, nowUtc);
    }

    public void RemoveAvailabilityWindow(Guid availabilityWindowId, Guid actorUserId, DateTime nowUtc)
    {
        if (_availabilityWindows.RemoveAll(w => w.Id == availabilityWindowId) > 0)
            Touch(actorUserId, nowUtc);
    }

    public void Archive(Guid actorUserId, DateTime nowUtc)
    {
        IsArchived = true;
        Touch(actorUserId, nowUtc);
    }

    private void Touch(Guid actorUserId, DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }

    // EF Core owned-collection backing for the ResourceApprovers table — see
    // User.RoleAssignment for why this is public and behavior-free.
    public sealed class ApproverAssignment
    {
        public Guid UserId { get; private set; }

        private ApproverAssignment()
        {
        }

        public ApproverAssignment(Guid userId)
        {
            UserId = userId;
        }
    }
}
