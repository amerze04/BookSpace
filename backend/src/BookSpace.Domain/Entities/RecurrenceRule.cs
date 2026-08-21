using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// FR-5.1 daily/weekly/monthly + interval + end condition.
// FR-6.2 TimeZoneId is the zone the rule expands in; expansion itself
// happens in the Application layer, in local wall-clock time, then converts
// to UTC per CLAUDE.md §4.3 — this entity only holds the rule's shape.
// Decision #7 (docs/decisions/0007): a series runs at most two calendar years
// past its own StartDate — occurrences are materialized in full at creation
// (best-effort per occurrence), not by a background top-up job. The EndDate
// case is also enforced in the DB (CK_RecurrenceRules_MaxSpan); the
// OccurrenceCount case can only be checked here, since "implied span" for
// Monthly recurrence isn't a clean single SQL expression across frequencies.
public class RecurrenceRule : IAuditable
{
    public Guid Id { get; private set; }
    public Guid ResourceId { get; private set; }
    public Guid UserId { get; private set; }
    public RecurrenceFrequency Frequency { get; private set; }
    public int IntervalValue { get; private set; }
    public TimeOnly LocalStartTime { get; private set; }
    public TimeOnly LocalEndTime { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? OccurrenceCount { get; private set; }
    public string TimeZoneId { get; private set; }
    public RecurrenceStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // EF Core materialization only — see Organization.cs for why this is needed.
    private RecurrenceRule()
    {
        TimeZoneId = string.Empty;
    }

    public RecurrenceRule(
        Guid id,
        Guid resourceId,
        Guid userId,
        RecurrenceFrequency frequency,
        int intervalValue,
        TimeOnly localStartTime,
        TimeOnly localEndTime,
        DateOnly startDate,
        DateOnly? endDate,
        int? occurrenceCount,
        string timeZoneId,
        Guid createdByUserId,
        DateTime nowUtc)
    {
        if (intervalValue <= 0) // CK_RecurrenceRules_Interval
            throw new ArgumentOutOfRangeException(nameof(intervalValue), "IntervalValue must be greater than zero.");
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("TimeZoneId is required.", nameof(timeZoneId));
        // CK_RecurrenceRules_EndCondition: exactly one of EndDate / OccurrenceCount
        if ((endDate is null) == (occurrenceCount is null))
            throw new ArgumentException("Exactly one of EndDate or OccurrenceCount must be set.");

        var impliedEndDate = ComputeImpliedEndDate(frequency, intervalValue, startDate, endDate, occurrenceCount);
        if (impliedEndDate > startDate.AddYears(2)) // CK_RecurrenceRules_MaxSpan (Decision #7)
            throw new ArgumentException("A recurrence series cannot run more than two years past its StartDate.");

        Id = id;
        ResourceId = resourceId;
        UserId = userId;
        Frequency = frequency;
        IntervalValue = intervalValue;
        LocalStartTime = localStartTime;
        LocalEndTime = localEndTime;
        StartDate = startDate;
        EndDate = endDate;
        OccurrenceCount = occurrenceCount;
        TimeZoneId = timeZoneId;
        Status = RecurrenceStatus.Active;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

    public void Cancel(Guid actorUserId, DateTime nowUtc)
    {
        Status = RecurrenceStatus.Cancelled;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }

    // Decision #7 span cap: for an EndDate-bound rule this is just EndDate;
    // for an OccurrenceCount-bound rule it's the date of the last occurrence,
    // computed the same way full materialization will (last occurrence is
    // IntervalValue * (OccurrenceCount - 1) steps after StartDate).
    private static DateOnly ComputeImpliedEndDate(
        RecurrenceFrequency frequency,
        int intervalValue,
        DateOnly startDate,
        DateOnly? endDate,
        int? occurrenceCount)
    {
        if (endDate is not null)
            return endDate.Value;

        var steps = intervalValue * (occurrenceCount!.Value - 1);
        return frequency switch
        {
            RecurrenceFrequency.Daily => startDate.AddDays(steps),
            RecurrenceFrequency.Weekly => startDate.AddDays(steps * 7),
            RecurrenceFrequency.Monthly => startDate.AddMonths(steps),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
    }
}
