using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// FR-1.1 root of tenant ownership; FR-1.3 suspend/reactivate;
// FR-7.4 / FR-8.3 / FR-9.1 org-level scheduling defaults live here.
public class Organization : IAuditable
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Slug { get; private set; }
    public string TimeZoneId { get; private set; }
    public OrganizationStatus Status { get; private set; }
    public int ReminderLeadMinutes { get; private set; }
    public int NoShowGraceMinutes { get; private set; }
    public int? ApprovalExpiryHours { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // EF Core materialization only — nowUtc/createdByUserId below each set two
    // properties, so EF's constructor-binding can't use the validating
    // constructor to hydrate a row read back from the database. EF overwrites
    // these placeholder values immediately after construction.
    private Organization()
    {
        Name = string.Empty;
        Slug = string.Empty;
        TimeZoneId = string.Empty;
    }

    public Organization(
        Guid id,
        string name,
        string slug,
        string timeZoneId,
        int reminderLeadMinutes,
        int noShowGraceMinutes,
        int? approvalExpiryHours,
        Guid createdByUserId,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug is required.", nameof(slug));
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("TimeZoneId is required.", nameof(timeZoneId));

        Id = id;
        Name = name;
        Slug = slug;
        TimeZoneId = timeZoneId;
        Status = OrganizationStatus.Active;
        ReminderLeadMinutes = reminderLeadMinutes;
        NoShowGraceMinutes = noShowGraceMinutes;
        ApprovalExpiryHours = approvalExpiryHours;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

    public void Suspend(Guid actorUserId, DateTime nowUtc) =>
        SetStatus(OrganizationStatus.Suspended, actorUserId, nowUtc);

    public void Reactivate(Guid actorUserId, DateTime nowUtc) =>
        SetStatus(OrganizationStatus.Active, actorUserId, nowUtc);

    private void SetStatus(OrganizationStatus status, Guid actorUserId, DateTime nowUtc)
    {
        Status = status;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }
}
