namespace BookSpace.Domain.Entities;

// FR-3.2 recurring open hours, resource-local wall clock — see
// docs/decisions/0003-availability-timezone.md. No audit columns: entries
// are typically bulk-replaced as a weekly set rather than individually
// edited (excluded per CLAUDE.md §9's audit-column instruction).
public class AvailabilityWindow
{
    public Guid Id { get; private set; }
    public Guid ResourceId { get; private set; }
    public DayOfWeek Weekday { get; private set; }
    public TimeOnly OpensAt { get; private set; }
    public TimeOnly ClosesAt { get; private set; }

    public AvailabilityWindow(Guid id, Guid resourceId, DayOfWeek weekday, TimeOnly opensAt, TimeOnly closesAt)
    {
        if (closesAt <= opensAt) // CK_AvailabilityWindows_Window
            throw new ArgumentException("ClosesAt must be after OpensAt.", nameof(closesAt));

        Id = id;
        ResourceId = resourceId;
        Weekday = weekday;
        OpensAt = opensAt;
        ClosesAt = closesAt;
    }
}
