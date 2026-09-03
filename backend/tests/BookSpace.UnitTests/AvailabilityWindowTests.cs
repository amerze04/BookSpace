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

    // A window's own invariants, exercised through ReplaceAvailabilityWindows —
    // which is the only way to set a schedule since WP-3 Phase 5 step 4 deleted
    // the per-window AddAvailabilityWindow. What is asserted here is unchanged:
    // the constructor these go through is the same one, and it is still the only
    // place a window can be built.

    [Fact]
    public void AWindowKeepsTheWeekdayAndTimesItWasGiven()
    {
        var resource = CreateResource();
        var id = Guid.NewGuid();
        var opens = new TimeOnly(9, 0);
        var closes = new TimeOnly(17, 0);

        var window = Assert.Single(resource.ReplaceAvailabilityWindows(
            [new AvailabilityWindowDefinition(id, DayOfWeek.Monday, opens, closes)],
            ActorId,
            NowUtc));

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
    public void AWindowIsStampedWithItsOwningResourcesOrgId()
    {
        var resource = CreateResource();

        var window = Assert.Single(resource.ReplaceAvailabilityWindows(
            [new AvailabilityWindowDefinition(
                Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0))],
            ActorId,
            NowUtc));

        Assert.Equal(resource.OrgId, window.OrgId);
        Assert.Equal(resource.OrgId, ((ITenantOwned)window).OrgId);
    }

    // CK_AvailabilityWindows_Window, restated in the constructor. Equal is
    // refused as well as inverted: a zero-width window opens at no time.
    [Theory]
    [InlineData(9, 0, 9, 0)]
    [InlineData(17, 0, 9, 0)]
    public void AWindowThatDoesNotCloseAfterItOpensIsRefused(
        int opensHour, int opensMinute, int closesHour, int closesMinute)
    {
        var resource = CreateResource();

        Assert.Throws<ArgumentException>(() => resource.ReplaceAvailabilityWindows(
            [new AvailabilityWindowDefinition(
                Guid.NewGuid(),
                DayOfWeek.Monday,
                new TimeOnly(opensHour, opensMinute),
                new TimeOnly(closesHour, closesMinute))],
            ActorId,
            NowUtc));

        // The interval check runs in the constructor, while the replacement is
        // still being built, so a rejected window leaves the collection
        // untouched rather than half-replaced.
        Assert.Empty(resource.AvailabilityWindows);
    }

    // A prior schedule for a replacement to overwrite, set through the same
    // method under test. This file does not use the shared
    // ResourceScheduleArrangement shim on purpose: it is *about* the weekly
    // schedule, so its arrangement should go through the real API too.
    private static Guid GiveMondayNineToFive(Resource resource)
    {
        var id = Guid.NewGuid();

        resource.ReplaceAvailabilityWindows(
            [new AvailabilityWindowDefinition(
                id, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0))],
            ActorId,
            NowUtc);

        return id;
    }

    // ---- ReplaceAvailabilityWindows (WP-3 Phase 3, FR-3.2) ----

    [Fact]
    public void ReplaceAvailabilityWindows_ReplacesTheWholeSet()
    {
        var resource = CreateResource();
        GiveMondayNineToFive(resource);

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
        GiveMondayNineToFive(resource);

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
        var originalId = GiveMondayNineToFive(resource);

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
