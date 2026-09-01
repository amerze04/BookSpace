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
        ValidateName(name);
        ValidateResourceType(resourceType);
        ValidateTimeZoneId(timeZoneId);
        ValidateCapacity(capacity);
        ValidateDurationLimits(minDurationMinutes, maxDurationMinutes);

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

    // ---- Edit methods (WP-3 Phase 2, FR-3.1) --------------------------------
    //
    // Grouped the way an admin edits them rather than one setter per column:
    // an edit payload is a full representation (docs/decisions/0015-api-
    // contract-and-pagination.md), so a write handler calls several of these
    // in sequence against one loaded aggregate.
    //
    // Two rules FR-3.1/FR-3.5 do impose are deliberately NOT enforced here:
    // an archived resource refusing edits (ReasonCodes.ResourceArchived) and
    // RequiresApproval needing at least one approver
    // (ReasonCodes.ApproversRequired). Both have to reach the client as a
    // machine-readable reason code (FR-4.5), which only an AppException from
    // the Application layer carries — a domain throw would surface as a 500.
    // They are tier-4 rules under CLAUDE.md §6, and Phase 2's write handlers
    // own them.

    public void UpdateDetails(
        string name,
        string? description,
        string resourceType,
        Guid actorUserId,
        DateTime nowUtc)
    {
        ValidateName(name);
        ValidateResourceType(resourceType);

        Name = name;
        Description = description;
        ResourceType = resourceType;
        Touch(actorUserId, nowUtc);
    }

    // The entity guarantees only capacity > 0 (CK_Resources_Capacity). Whether
    // a decrease strands existing bookings is a query over another aggregate,
    // not an invariant of this one, so it stays in the Application layer as
    // CapacityBelowExistingBookings.
    public void ChangeCapacity(int capacity, Guid actorUserId, DateTime nowUtc)
    {
        ValidateCapacity(capacity);

        Capacity = capacity;
        Touch(actorUserId, nowUtc);
    }

    // Reinterprets every existing availability window, which is stored as
    // resource-local wall-clock time (docs/decisions/0003-availability-
    // timezone.md): the windows are untouched, the instants they mean move.
    // Saying so explicitly is the response's job (wp3-plan's "smaller calls");
    // the entity only records the new zone. Non-blank is all that is checked
    // here — whether the string is a real IANA id is InvalidTimeZone, resolved
    // in the Application layer, since Domain takes no dependency on the
    // platform's timezone database.
    public void ChangeTimeZone(string timeZoneId, Guid actorUserId, DateTime nowUtc)
    {
        ValidateTimeZoneId(timeZoneId);

        TimeZoneId = timeZoneId;
        Touch(actorUserId, nowUtc);
    }

    // Both bounds are optional and independent: either, neither, or both. Null
    // means "no limit", which is why the pair is replaced in one call rather
    // than through two setters that could cross mid-edit.
    public void SetDurationLimits(
        int? minDurationMinutes,
        int? maxDurationMinutes,
        Guid actorUserId,
        DateTime nowUtc)
    {
        ValidateDurationLimits(minDurationMinutes, maxDurationMinutes);

        MinDurationMinutes = minDurationMinutes;
        MaxDurationMinutes = maxDurationMinutes;
        Touch(actorUserId, nowUtc);
    }

    // FR-3.3. Setting this true with an empty approver list is refused by the
    // Application layer (ApproversRequired), not here — see the note above.
    public void SetRequiresApproval(bool requiresApproval, Guid actorUserId, DateTime nowUtc)
    {
        RequiresApproval = requiresApproval;
        Touch(actorUserId, nowUtc);
    }

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

    // WP-3 decision D1: this is the only creator of AvailabilityWindow — its
    // constructor is internal to the Domain assembly — so a window can never
    // carry an OrgId that disagrees with its resource's. Returns the created
    // window so a caller can shape a response from it without re-reading.
    public AvailabilityWindow AddAvailabilityWindow(
        Guid availabilityWindowId,
        DayOfWeek weekday,
        TimeOnly opensAt,
        TimeOnly closesAt,
        Guid actorUserId,
        DateTime nowUtc)
    {
        var window = new AvailabilityWindow(availabilityWindowId, OrgId, Id, weekday, opensAt, closesAt);
        _availabilityWindows.Add(window);
        Touch(actorUserId, nowUtc);
        return window;
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

    // Shared by the constructor and the edit methods, so a value that could not
    // be constructed cannot be assigned later either. Two places enforcing the
    // same invariant separately is how the two drift apart.

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
    }

    private static void ValidateResourceType(string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType))
            throw new ArgumentException("ResourceType is required.", nameof(resourceType));
    }

    private static void ValidateTimeZoneId(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("TimeZoneId is required.", nameof(timeZoneId));
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity <= 0) // CK_Resources_Capacity
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
    }

    // Resources has no CHECK constraint for this pair — flagged to the owner
    // rather than added on judgment, since the schema doc is the source of
    // truth (CLAUDE.md §11). Enforced here meanwhile so the two bounds cannot
    // be set incoherently through the API.
    private static void ValidateDurationLimits(int? minDurationMinutes, int? maxDurationMinutes)
    {
        if (minDurationMinutes is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(minDurationMinutes),
                "MinDurationMinutes must be greater than zero when supplied.");
        if (maxDurationMinutes is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxDurationMinutes),
                "MaxDurationMinutes must be greater than zero when supplied.");
        if (minDurationMinutes is not null &&
            maxDurationMinutes is not null &&
            maxDurationMinutes < minDurationMinutes)
        {
            throw new ArgumentException(
                "MaxDurationMinutes must be greater than or equal to MinDurationMinutes.",
                nameof(maxDurationMinutes));
        }
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
