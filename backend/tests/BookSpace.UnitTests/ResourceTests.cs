using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

public class ResourceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static Resource CreateValid(int capacity = 8, bool requiresApproval = false) =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", "Room", capacity, "America/New_York",
            requiresApproval, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: NowUtc);

    [Fact]
    public void Constructor_SetsExpectedDefaults()
    {
        var resource = CreateValid();

        Assert.False(resource.IsArchived);
        Assert.Empty(resource.ApproverUserIds);
        Assert.Empty(resource.AvailabilityWindows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankName(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new Resource(Guid.NewGuid(), OrgId, name, "Room", 8, "America/New_York", false, null, null, null, ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankResourceType(string resourceType)
    {
        Assert.Throws<ArgumentException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", resourceType, 8, "America/New_York", false, null, null, null, ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankTimeZoneId(string timeZoneId)
    {
        Assert.Throws<ArgumentException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", "Room", 8, timeZoneId, false, null, null, null, ActorId, NowUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ThrowsOnNonPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateValid(capacity: capacity));
    }

    [Fact]
    public void AddApprover_AddsUserAndTouchesAuditFields()
    {
        var resource = CreateValid(requiresApproval: true);
        var approverId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        resource.AddApprover(approverId, actor, later);

        Assert.Contains(approverId, resource.ApproverUserIds);
        Assert.Equal(actor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    [Fact]
    public void AddApprover_IsIdempotent()
    {
        var resource = CreateValid(requiresApproval: true);
        var approverId = Guid.NewGuid();

        resource.AddApprover(approverId, ActorId, NowUtc);
        resource.AddApprover(approverId, ActorId, NowUtc);

        Assert.Single(resource.ApproverUserIds);
    }

    [Fact]
    public void RemoveApprover_RemovesUser()
    {
        var resource = CreateValid(requiresApproval: true);
        var approverId = Guid.NewGuid();
        resource.AddApprover(approverId, ActorId, NowUtc);

        resource.RemoveApprover(approverId, ActorId, NowUtc.AddMinutes(1));

        Assert.DoesNotContain(approverId, resource.ApproverUserIds);
    }

    [Fact]
    public void AddAvailabilityWindow_AddsWindow()
    {
        var resource = CreateValid();

        var window = resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        Assert.Contains(window, resource.AvailabilityWindows);
    }

    [Fact]
    public void RemoveAvailabilityWindow_RemovesById()
    {
        var resource = CreateValid();
        var window = resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        resource.RemoveAvailabilityWindow(window.Id, ActorId, NowUtc.AddMinutes(1));

        Assert.Empty(resource.AvailabilityWindows);
    }

    [Fact]
    public void Archive_SetsIsArchivedTrue()
    {
        var resource = CreateValid();

        resource.Archive(ActorId, NowUtc.AddMinutes(1));

        Assert.True(resource.IsArchived);
    }
}
