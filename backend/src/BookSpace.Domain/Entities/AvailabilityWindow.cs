using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

// FR-3.2 recurring open hours, resource-local wall clock — see
// docs/decisions/0003-availability-timezone.md. No audit columns: entries
// are typically bulk-replaced as a weekly set rather than individually
// edited (excluded per CLAUDE.md §9's audit-column instruction).
//
// OrgId is denormalized from the owning Resource (WP-3 decision D1, following
// the precedent of docs/decisions/0006-orgid-denormalization.md) so this table
// sits inside all three CLAUDE.md §4.2 isolation mechanisms instead of being
// reachable by ResourceId alone. The constructor is internal and Resource
// .AddAvailabilityWindow is the only caller: a window whose OrgId disagrees
// with its resource's is then unconstructible, rather than merely caught later
// by the SaveChanges guard and the composite FK.
public class AvailabilityWindow : ITenantOwned
{
    public Guid Id { get; private set; }
    public Guid OrgId { get; private set; }
    public Guid ResourceId { get; private set; }
    public DayOfWeek Weekday { get; private set; }
    public TimeOnly OpensAt { get; private set; }
    public TimeOnly ClosesAt { get; private set; }

    internal AvailabilityWindow(
        Guid id,
        Guid orgId,
        Guid resourceId,
        DayOfWeek weekday,
        TimeOnly opensAt,
        TimeOnly closesAt)
    {
        if (closesAt <= opensAt) // CK_AvailabilityWindows_Window
            throw new ArgumentException("ClosesAt must be after OpensAt.", nameof(closesAt));

        Id = id;
        OrgId = orgId;
        ResourceId = resourceId;
        Weekday = weekday;
        OpensAt = opensAt;
        ClosesAt = closesAt;
    }

    Guid? ITenantOwned.OrgId => OrgId;
}
