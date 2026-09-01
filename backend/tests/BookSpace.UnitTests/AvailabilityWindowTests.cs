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
}
