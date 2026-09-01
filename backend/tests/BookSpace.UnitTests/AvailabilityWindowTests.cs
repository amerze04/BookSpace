using BookSpace.Domain.Common;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

// AvailabilityWindow's constructor is internal to the Domain assembly (WP-3
// decision D1) — Resource is its only creator — so these tests exercise it
// through the aggregate, which is the path production code takes.
public class AvailabilityWindowTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static Resource CreateResource() =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", "Room", capacity: 8,
            timeZoneId: "America/New_York", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

    [Fact]
    public void AddAvailabilityWindow_SetsProperties_WhenClosesAtIsAfterOpensAt()
    {
        var resource = CreateResource();
        var id = Guid.NewGuid();
        var opens = new TimeOnly(9, 0);
        var closes = new TimeOnly(17, 0);

        var window = resource.AddAvailabilityWindow(id, DayOfWeek.Monday, opens, closes, ActorId, NowUtc);

        Assert.Equal(id, window.Id);
        Assert.Equal(resource.Id, window.ResourceId);
        Assert.Equal(DayOfWeek.Monday, window.Weekday);
        Assert.Equal(opens, window.OpensAt);
        Assert.Equal(closes, window.ClosesAt);
    }

    // The whole point of the factory: OrgId comes from the owning resource,
    // never from the caller, so a window scoped to a tenant its resource isn't
    // in cannot be constructed at all.
    [Fact]
    public void AddAvailabilityWindow_StampsOwningResourceOrgId()
    {
        var resource = CreateResource();

        var window = resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        Assert.Equal(resource.OrgId, window.OrgId);
        Assert.Equal(resource.OrgId, ((ITenantOwned)window).OrgId);
    }

    [Fact]
    public void AddAvailabilityWindow_Throws_WhenClosesAtEqualsOpensAt()
    {
        var resource = CreateResource();
        var time = new TimeOnly(9, 0);

        Assert.Throws<ArgumentException>(() =>
            resource.AddAvailabilityWindow(Guid.NewGuid(), DayOfWeek.Monday, time, time, ActorId, NowUtc));

        // The interval check runs in the constructor, before the window is
        // added, so a rejected window leaves the collection untouched.
        Assert.Empty(resource.AvailabilityWindows);
    }

    [Fact]
    public void AddAvailabilityWindow_Throws_WhenClosesAtIsBeforeOpensAt()
    {
        var resource = CreateResource();

        Assert.Throws<ArgumentException>(() => resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(17, 0), new TimeOnly(9, 0), ActorId, NowUtc));

        Assert.Empty(resource.AvailabilityWindows);
    }

    // ---- ReplaceAvailabilityWindows (WP-3 Phase 3, FR-3.2) ----

    [Fact]
    public void ReplaceAvailabilityWindows_ReplacesTheWholeSet()
    {
        var resource = CreateResource();
        resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        var tuesdayId = Guid.NewGuid();
        var replaced = resource.ReplaceAvailabilityWindows(
            new[]
            {
                new AvailabilityWindowDefinition(
                    tuesdayId, DayOfWeek.Tuesday, new TimeOnly(8, 0), new TimeOnly(12, 0)),
            },
            ActorId,
            NowUtc.AddMinutes(1));

        // Replace, not merge: Monday is gone because it was not resent.
        var window = Assert.Single(resource.AvailabilityWindows);
        Assert.Equal(tuesdayId, window.Id);
        Assert.Equal(DayOfWeek.Tuesday, window.Weekday);
        Assert.Equal(resource.Id, window.ResourceId);
        Assert.Equal(OrgId, window.OrgId);
        Assert.Equal(resource.AvailabilityWindows, replaced);
    }

    // An empty set is a legitimate schedule meaning "opens at no time at all",
    // not a malformed request — see the validator for why the two are kept apart.
    [Fact]
    public void ReplaceAvailabilityWindows_WithAnEmptySet_ClearsTheSchedule()
    {
        var resource = CreateResource();
        resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        resource.ReplaceAvailabilityWindows(
            Array.Empty<AvailabilityWindowDefinition>(), ActorId, NowUtc.AddMinutes(1));

        Assert.Empty(resource.AvailabilityWindows);
    }

    [Fact]
    public void ReplaceAvailabilityWindows_TouchesTheAuditColumns()
    {
        var resource = CreateResource();
        var editor = Guid.NewGuid();
        var later = NowUtc.AddHours(3);

        resource.ReplaceAvailabilityWindows(
            new[]
            {
                new AvailabilityWindowDefinition(
                    Guid.NewGuid(), DayOfWeek.Friday, new TimeOnly(9, 0), new TimeOnly(10, 0)),
            },
            editor,
            later);

        // The windows carry no audit columns of their own, so the resource's are
        // the only record that the schedule changed.
        Assert.Equal(editor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    // The replacement is built before anything is removed, so a window that fails
    // CK_AvailabilityWindows_Window leaves the previous schedule intact rather
    // than half-applied. Without that ordering the resource would end up with no
    // windows at all after a rejected request.
    [Fact]
    public void ReplaceAvailabilityWindows_WithAnInvalidWindow_LeavesTheExistingScheduleUntouched()
    {
        var resource = CreateResource();
        var originalId = Guid.NewGuid();
        resource.AddAvailabilityWindow(
            originalId, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        Assert.Throws<ArgumentException>(() => resource.ReplaceAvailabilityWindows(
            new[]
            {
                new AvailabilityWindowDefinition(
                    Guid.NewGuid(), DayOfWeek.Tuesday, new TimeOnly(8, 0), new TimeOnly(12, 0)),
                new AvailabilityWindowDefinition(
                    Guid.NewGuid(), DayOfWeek.Wednesday, new TimeOnly(17, 0), new TimeOnly(9, 0)),
            },
            ActorId,
            NowUtc.AddMinutes(1)));

        var window = Assert.Single(resource.AvailabilityWindows);
        Assert.Equal(originalId, window.Id);
        Assert.Equal(NowUtc, resource.UpdatedAtUtc);
    }
}
