using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

// FR-3.4 blackouts override availability; absolute instants.
// Absolute priority over recurring series — see
// docs/decisions/0001-blackout-vs-recurring-series.md. Cancelling the
// occurrences a blackout overlaps is an Application-layer orchestration
// (it spans the Booking aggregate too), not something this entity does on
// its own — Overlaps() below is the pure predicate that orchestration needs.
public class BlackoutPeriod : IAuditable
{
    public Guid Id { get; private set; }
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
        ResourceId = resourceId;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Reason = reason;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

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
