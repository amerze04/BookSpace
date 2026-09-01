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

    [Theory]
    [InlineData(0, 240)]
    [InlineData(-1, 240)]
    [InlineData(30, 0)]
    [InlineData(30, -1)]
    public void Constructor_ThrowsOnNonPositiveDurationLimit(int min, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", "Room", 8, "America/New_York", false, min, max, null, ActorId, NowUtc));
    }

    [Fact]
    public void Constructor_ThrowsWhenMaxDurationIsBelowMin()
    {
        Assert.Throws<ArgumentException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", "Room", 8, "America/New_York", false, 120, 60, null, ActorId, NowUtc));
    }

    // ---- Edit methods (WP-3 Phase 2 step 1, FR-3.1) ----

    [Fact]
    public void UpdateDetails_ReplacesFieldsAndTouchesAuditFields()
    {
        var resource = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        resource.UpdateDetails("Board Room", "Top floor", "MeetingRoom", actor, later);

        Assert.Equal("Board Room", resource.Name);
        Assert.Equal("Top floor", resource.Description);
        Assert.Equal("MeetingRoom", resource.ResourceType);
        Assert.Equal(actor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    // Edits are full representations (docs/decisions/0015), so a null
    // description clears it rather than meaning "leave as is".
    [Fact]
    public void UpdateDetails_ClearsDescriptionWhenNull()
    {
        var resource = CreateValid();

        resource.UpdateDetails("Conference Room A", null, "Room", ActorId, NowUtc.AddMinutes(1));

        Assert.Null(resource.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateDetails_ThrowsOnBlankName(string name)
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            resource.UpdateDetails(name, null, "Room", ActorId, NowUtc.AddMinutes(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateDetails_ThrowsOnBlankResourceType(string resourceType)
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            resource.UpdateDetails("Room A", null, resourceType, ActorId, NowUtc.AddMinutes(1)));
    }

    [Fact]
    public void UpdateDetails_LeavesNameUnchangedWhenItThrows()
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            resource.UpdateDetails("Board Room", null, "  ", ActorId, NowUtc.AddMinutes(1)));

        Assert.Equal("Conference Room A", resource.Name);
    }

    [Fact]
    public void ChangeCapacity_UpdatesCapacityAndTouchesAuditFields()
    {
        var resource = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        resource.ChangeCapacity(3, actor, later);

        Assert.Equal(3, resource.Capacity);
        Assert.Equal(actor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ChangeCapacity_ThrowsOnNonPositiveCapacity(int capacity)
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resource.ChangeCapacity(capacity, ActorId, NowUtc.AddMinutes(1)));
    }

    // The windows themselves are resource-local wall-clock times
    // (docs/decisions/0003), so a zone change reinterprets them without
    // rewriting any of them.
    [Fact]
    public void ChangeTimeZone_UpdatesZoneAndLeavesAvailabilityWindowsUntouched()
    {
        var resource = CreateValid();
        var window = resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, NowUtc);

        resource.ChangeTimeZone("Europe/Zagreb", ActorId, NowUtc.AddMinutes(1));

        Assert.Equal("Europe/Zagreb", resource.TimeZoneId);
        Assert.Equal(new TimeOnly(9, 0), window.OpensAt);
        Assert.Equal(new TimeOnly(17, 0), window.ClosesAt);
        Assert.Single(resource.AvailabilityWindows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeTimeZone_ThrowsOnBlankTimeZoneId(string timeZoneId)
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            resource.ChangeTimeZone(timeZoneId, ActorId, NowUtc.AddMinutes(1)));
    }

    [Fact]
    public void SetDurationLimits_ReplacesBothBoundsAndTouchesAuditFields()
    {
        var resource = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        resource.SetDurationLimits(15, 60, actor, later);

        Assert.Equal(15, resource.MinDurationMinutes);
        Assert.Equal(60, resource.MaxDurationMinutes);
        Assert.Equal(actor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    [Fact]
    public void SetDurationLimits_AllowsBothNullMeaningNoLimit()
    {
        var resource = CreateValid();

        resource.SetDurationLimits(null, null, ActorId, NowUtc.AddMinutes(1));

        Assert.Null(resource.MinDurationMinutes);
        Assert.Null(resource.MaxDurationMinutes);
    }

    [Theory]
    [InlineData(30, null)]
    [InlineData(null, 240)]
    public void SetDurationLimits_AllowsOneBoundWithoutTheOther(int? min, int? max)
    {
        var resource = CreateValid();

        resource.SetDurationLimits(min, max, ActorId, NowUtc.AddMinutes(1));

        Assert.Equal(min, resource.MinDurationMinutes);
        Assert.Equal(max, resource.MaxDurationMinutes);
    }

    // A fixed-length booking is a legitimate configuration, so equal bounds are
    // allowed — only max < min is not.
    [Fact]
    public void SetDurationLimits_AllowsEqualBounds()
    {
        var resource = CreateValid();

        resource.SetDurationLimits(60, 60, ActorId, NowUtc.AddMinutes(1));

        Assert.Equal(60, resource.MinDurationMinutes);
        Assert.Equal(60, resource.MaxDurationMinutes);
    }

    [Fact]
    public void SetDurationLimits_ThrowsWhenMaxIsBelowMin()
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            resource.SetDurationLimits(120, 60, ActorId, NowUtc.AddMinutes(1)));
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(30, 0)]
    [InlineData(-5, null)]
    public void SetDurationLimits_ThrowsOnNonPositiveBound(int? min, int? max)
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resource.SetDurationLimits(min, max, ActorId, NowUtc.AddMinutes(1)));
    }

    [Fact]
    public void SetRequiresApproval_TogglesFlagAndTouchesAuditFields()
    {
        var resource = CreateValid(requiresApproval: false);
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);
        resource.AddApprover(Guid.NewGuid(), ActorId, NowUtc);

        resource.SetRequiresApproval(true, actor, later);

        Assert.True(resource.RequiresApproval);
        Assert.Equal(actor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);

        resource.SetRequiresApproval(false, actor, later);

        Assert.False(resource.RequiresApproval);
    }

    // Documents where the FR-3.3 invariant lives: the entity does not refuse
    // this, because the refusal has to carry ReasonCodes.ApproversRequired,
    // which only the Application layer can raise (CLAUDE.md §6, tier 4).
    [Fact]
    public void SetRequiresApproval_TrueWithNoApprovers_IsNotRefusedByTheEntity()
    {
        var resource = CreateValid(requiresApproval: false);

        resource.SetRequiresApproval(true, ActorId, NowUtc.AddMinutes(1));

        Assert.True(resource.RequiresApproval);
        Assert.Empty(resource.ApproverUserIds);
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
