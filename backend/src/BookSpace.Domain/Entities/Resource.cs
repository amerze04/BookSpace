using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;
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
    public ResourceType ResourceType { get; private set; }
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

    // Hardening pass. Optimistic concurrency, the same mechanism CLAUDE.md §5
    // already uses on Bookings.RowVersion — added because SetRequiresApproval
    // and ReplaceApprovers (FR-3.3's invariant, split across two endpoints)
    // otherwise race: both read-check-mutate-save with no serialization
    // between them, so two concurrent requests can each see the invariant
    // satisfied under the *other's* about-to-be-superseded state and both
    // commit, leaving RequiresApproval = true with an empty approver list.
    // Every mutator here already calls Touch() — including ReplaceApprovers,
    // which otherwise touches only the owned ResourceApprovers table — so
    // every write that matters for this invariant issues an UPDATE against
    // this row guarded by this token, and the loser gets
    // DbUpdateConcurrencyException (already mapped to 409, same as Bookings).
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    // EF Core materialization only — see Organization.cs for why this is needed.
    private Resource()
    {
        Name = string.Empty;
        TimeZoneId = string.Empty;
    }

    public Resource(
        Guid id,
        Guid orgId,
        string name,
        ResourceType resourceType,
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
    // One rule FR-3.5 does impose is deliberately NOT enforced here: an
    // archived resource refusing edits (ReasonCodes.ResourceArchived). It has to
    // reach the client as a machine-readable reason code (FR-4.5), which only an
    // AppException from the Application layer carries — a domain throw would
    // surface as a 500. It is a tier-4 rule under CLAUDE.md §6, and the write
    // handlers own it.
    //
    // "RequiresApproval needs at least one approver" used to sit alongside it
    // and is gone entirely — decision 0028. The entity was already free to hold
    // that state; now so is the system.

    public void UpdateDetails(
        string name,
        string? description,
        ResourceType resourceType,
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

    // ---- The duration limits, as questions (2026-09-04) -------------------
    //
    // Two questions, not one, and keeping them apart is the point. Until now
    // MinDurationMinutes was read in exactly one place — the availability query's
    // filter — and MaxDurationMinutes was read *nowhere at all*: stored,
    // constrained by CK_Resources_DurationLimits, echoed back in responses, and
    // never enforced. WP-4 has to enforce both on a booking, at which point the
    // minimum would have existed in two implementations.
    //
    // So both questions live here, on the aggregate that owns the numbers, and
    // both callers ask rather than compare. Same reasoning that put the interval
    // algebra in this project rather than in a handler.

    // Read side: could *any* legal booking fit inside a span this long? Only the
    // minimum bears on it — a span longer than MaxDurationMinutes is not a
    // problem, because a booker takes a piece of it rather than the whole thing.
    // That asymmetry is exactly why this is a separate method from the one below
    // and not a reuse of it.
    public bool CanFitABooking(TimeSpan span) =>
        MinDurationMinutes is not { } minimum || span >= TimeSpan.FromMinutes(minimum);

    // Write side: is a booking of exactly this length allowed? Both bounds apply.
    // No caller yet — WP-4's booking creation is the first, and it exists now so
    // that when it arrives there is nothing to reimplement.
    public bool AllowsBookingDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        if (MinDurationMinutes is { } minimum && duration < TimeSpan.FromMinutes(minimum))
        {
            return false;
        }

        return MaxDurationMinutes is not { } maximum || duration <= TimeSpan.FromMinutes(maximum);
    }

    // FR-3.3. Setting this true with an empty approver list is allowed, here
    // and everywhere else, since decision 0028: the resource is gated and its
    // approval requests fall back to the tenant's admins until approvers are
    // assigned.
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

    // FR-3.3, replace-the-set semantics, matching ReplaceAvailabilityWindows and
    // the PUT that drives it: the argument is the resource's entire approver list
    // afterwards.
    //
    // Set semantics, so a repeated id collapses — but the Application validator
    // rejects duplicates before they reach here, on the same principle as an
    // oversized pageSize: quietly accepting a payload and storing something else
    // is the behaviour being avoided.
    //
    // An empty set is a legitimate request, on a gated resource as much as any
    // other (decision 0028). Whether each id is even eligible is not knowable
    // here: it is a query over Users, so the Application layer owns it.
    public void ReplaceApprovers(IEnumerable<Guid> userIds, Guid actorUserId, DateTime nowUtc)
    {
        var replacement = userIds.Distinct().Select(id => new ApproverAssignment(id)).ToList();

        _approverAssignments.Clear();
        _approverAssignments.AddRange(replacement);
        Touch(actorUserId, nowUtc);
    }

    // FR-3.2, replace-the-set semantics: the argument is the resource's entire
    // weekly schedule afterwards. An empty set is legal and means the resource
    // currently opens at no time at all.
    //
    // WP-3 decision D1: this is the only creator of AvailabilityWindow — its
    // constructor is internal to the Domain assembly — so a window can never
    // carry an OrgId that disagrees with its resource's. It is also the *only*
    // way to change a schedule at all, since Phase 5 step 4 deleted the
    // per-window AddAvailabilityWindow and RemoveAvailabilityWindow: the API has
    // gone exclusively through this method since Phase 3, and the per-window add
    // carried a live EF trap (a window added to an already-tracked resource is
    // marked Modified, saves as a zero-row UPDATE, and surfaces as a 409 for what
    // is plainly an insert — see IResourceRepository.AddAvailabilityWindows).
    //
    // Rebuilt rather than diffed, which is what AvailabilityWindow's deliberate
    // lack of audit columns already assumes — entries are bulk-replaced as a
    // weekly set, so there is no per-row history to preserve and no id worth
    // keeping stable. Clearing the collection is what EF turns into DELETEs,
    // via the cascade on FK_AvailabilityWindows_Resources_SameOrg.
    //
    // The replacement is fully constructed before anything is removed, so a
    // window that fails CK_AvailabilityWindows_Window leaves the existing
    // schedule untouched rather than half-replaced — the same ordering the write
    // handlers use for their rule checks.
    //
    // Whether two windows on the same weekday overlap is NOT checked here. It
    // has to reach the client as ReasonCodes.OverlappingAvailabilityWindow, and
    // a domain throw arrives as a 500 carrying no code at all (see the note
    // above the edit methods). AvailabilityWindowRules owns it, exactly as the
    // Application layer owns approver eligibility.
    public IReadOnlyCollection<AvailabilityWindow> ReplaceAvailabilityWindows(
        IEnumerable<AvailabilityWindowDefinition> windows,
        Guid actorUserId,
        DateTime nowUtc)
    {
        var replacement = windows
            .Select(w => new AvailabilityWindow(w.Id, OrgId, Id, w.Weekday, w.OpensAt, w.ClosesAt))
            .ToList();

        _availabilityWindows.Clear();
        _availabilityWindows.AddRange(replacement);
        Touch(actorUserId, nowUtc);

        return AvailabilityWindows;
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

    // A closed set now, so "is it blank" is gone and the failure mode is
    // different: C# lets any int be cast to an enum, so `(ResourceType)99` would
    // otherwise be stored and then fail CK_Resources_ResourceType at the
    // database, as a 500 rather than a message. The Application validator
    // refuses an unknown value first (IsInEnum); this is the floor under it.
    private static void ValidateResourceType(ResourceType resourceType)
    {
        if (!Enum.IsDefined(resourceType))
            throw new ArgumentOutOfRangeException(nameof(resourceType), "ResourceType is not a known value.");
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
