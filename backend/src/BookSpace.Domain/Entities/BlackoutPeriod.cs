using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

// FR-3.4 blackouts override availability; absolute instants.
// Absolute priority over recurring series — see
// docs/decisions/0001-blackout-vs-recurring-series.md. Cancelling the
// occurrences a blackout overlaps is an Application-layer orchestration
// (it spans the Booking aggregate too), not something this entity does on
// its own — Overlaps() below is the pure predicate that orchestration needs.
//
// OrgId is denormalized from the owning Resource (WP-3 decision D1, following
// docs/decisions/0006-orgid-denormalization.md) so this table sits inside all
// three CLAUDE.md §4.2 isolation mechanisms instead of being reachable by
// ResourceId alone. Unlike AvailabilityWindow, a blackout is not part of the
// Resource aggregate — Resource has no navigation to it — so the constructor
// stays public and takes OrgId from the resource its caller loaded; the
// composite FK (OrgId, ResourceId) is what makes the two physically unable to
// disagree.
public class BlackoutPeriod : IAuditable, ITenantOwned
{
    public Guid Id { get; private set; }
    public Guid OrgId { get; private set; }
    public Guid ResourceId { get; private set; }
    public DateTime StartsAtUtc { get; private set; }
    public DateTime EndsAtUtc { get; private set; }
    public string? Reason { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // EF Core materialization only — see Organization.cs for why this is needed.
    private BlackoutPeriod()
    {
    }

    public BlackoutPeriod(
        Guid id,
        Guid orgId,
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        string? reason,
        Guid createdByUserId,
        DateTime nowUtc)
    {
        if (endsAtUtc <= startsAtUtc) // CK_BlackoutPeriods_Interval
            throw new ArgumentException("EndsAtUtc must be after StartsAtUtc.", nameof(endsAtUtc));

        Id = id;
        OrgId = orgId;
        ResourceId = resourceId;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Reason = reason;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

    Guid? ITenantOwned.OrgId => OrgId;

    public bool Overlaps(DateTime startsAtUtc, DateTime endsAtUtc) =>
        startsAtUtc < EndsAtUtc && StartsAtUtc < endsAtUtc;

    public void Reschedule(DateTime startsAtUtc, DateTime endsAtUtc, Guid actorUserId, DateTime nowUtc)
    {
        if (endsAtUtc <= startsAtUtc)
            throw new ArgumentException("EndsAtUtc must be after StartsAtUtc.", nameof(endsAtUtc));

        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }
}
