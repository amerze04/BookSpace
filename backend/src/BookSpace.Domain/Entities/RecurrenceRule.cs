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
//
// OrgId is denormalized from the owning Resource (decision 0025, following
// 0014's and 0006's precedent) so this table sits inside all three CLAUDE.md
// §4.2 isolation mechanisms instead of being reachable by id alone — which,
// before 0025, it was: WP-5 Phase 2's cancel endpoint is the first thing that
// ever loads a RecurrenceRule directly rather than only creating one scoped
// by the resource it belongs to, and that is exactly the shape 0014 already
// closed for AvailabilityWindows and BlackoutPeriods. Like BlackoutPeriod and
// unlike AvailabilityWindow, a RecurrenceRule is not part of the Resource
// aggregate — Resource has no navigation to it — so the constructor stays
// public and takes OrgId from the resource its caller already loaded; the
// composite FK (OrgId, ResourceId) is what makes the two physically unable
// to disagree.
public class RecurrenceRule : IAuditable, ITenantOwned
{
    public Guid Id { get; private set; }
    public Guid OrgId { get; private set; }
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
        Guid orgId,
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
        // No CK_RecurrenceRules constraint backs this — unlike AvailabilityWindow,
        // which CK_AvailabilityWindows_Window enforces at the DB too — so it is
        // stated here only. An occurrence is one calendar day's pair of local
        // times (RecurrenceExpansion, WP-5); FR-5.1 never asks for one that
        // crosses midnight, and nothing upstream computes what "the next day"
        // would even mean for a Monthly rule's occurrence date.
        if (localEndTime <= localStartTime)
            throw new ArgumentException("LocalEndTime must be after LocalStartTime.", nameof(localEndTime));
        // CK_RecurrenceRules_EndCondition: exactly one of EndDate / OccurrenceCount
        if ((endDate is null) == (occurrenceCount is null))
            throw new ArgumentException("Exactly one of EndDate or OccurrenceCount must be set.");

        var impliedEndDate = ComputeImpliedEndDate(frequency, intervalValue, startDate, endDate, occurrenceCount);
        if (impliedEndDate > startDate.AddYears(2)) // CK_RecurrenceRules_MaxSpan (Decision #7)
            throw new ArgumentException("A recurrence series cannot run more than two years past its StartDate.");

        Id = id;
        OrgId = orgId;
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

    Guid? ITenantOwned.OrgId => OrgId;

    // WP-5 Phase 2, FR-5.3: a series already cancelled has nothing left to
    // cancel again — the same reasoning Booking.CanBeCancelled states for a
    // second booking cancel, applied one level up. Stated as a predicate so
    // the handler can refuse with a reason code (RecurrenceRuleNotCancellable)
    // instead of catching Cancel's exception.
    public bool CanBeCancelled() => Status == RecurrenceStatus.Active;

    public void Cancel(Guid actorUserId, DateTime nowUtc)
    {
        if (!CanBeCancelled())
            throw new InvalidOperationException($"RecurrenceRule {Id} in status {Status} cannot be cancelled.");

        Status = RecurrenceStatus.Cancelled;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }

    // The date of the occurrence at this zero-based index in the series — WP-5's
    // RecurrenceExpansion walks the whole series with this rather than
    // re-deriving the daily/weekly/monthly stepping, which is also what
    // ComputeImpliedEndDate below uses for the span cap. One implementation,
    // two callers, so the two can never compute a different date for the same
    // index.
    public DateOnly OccurrenceDate(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), "index must not be negative.");

        // Hardening pass: checked, not the default unchecked int multiplication.
        // Unchecked, a large enough index * IntervalValue silently wraps to a
        // small or negative value instead of throwing — which could make a
        // step land somewhere other than where it visibly should, rather than
        // failing loudly the way an out-of-range value ought to. The
        // application-layer validator (CreateRecurrenceSeriesCommandRequestValidator)
        // is what actually keeps a real request out of this range; this is
        // defense in depth for any other caller (CLAUDE.md §6).
        checked
        {
            return StepDate(Frequency, StartDate, IntervalValue * index);
        }
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

        // Hardening pass: checked, for the identical reason OccurrenceDate is
        // above — this is the multiplication that decides whether the
        // two-year span cap even fires, so a silent wraparound here would not
        // just crash somewhere else, it would make an out-of-range series
        // pass the cap check it exists to enforce.
        checked
        {
            var steps = intervalValue * (occurrenceCount!.Value - 1);
            return StepDate(frequency, startDate, steps);
        }
    }

    // steps is already IntervalValue-scaled — the caller multiplies by
    // IntervalValue before this is reached, so this method only knows how to
    // walk a plain count of days/weeks/months.
    private static DateOnly StepDate(RecurrenceFrequency frequency, DateOnly startDate, int steps) =>
        frequency switch
        {
            RecurrenceFrequency.Daily => startDate.AddDays(steps),
            RecurrenceFrequency.Weekly => startDate.AddDays(steps * 7),
            RecurrenceFrequency.Monthly => startDate.AddMonths(steps),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
}
