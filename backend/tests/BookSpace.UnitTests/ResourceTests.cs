using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class ResourceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static Resource CreateValid(int capacity = 8, bool requiresApproval = false) =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, capacity, "America/New_York",
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
            new Resource(Guid.NewGuid(), OrgId, name, ResourceType.Room, 8, "America/New_York", false, null, null, null, ActorId, NowUtc));
    }

    // ResourceType became an enum on 2026-09-04, so "blank" is no longer a
    // possible input and the failure mode moved: C# lets any int be cast to an
    // enum, so an undefined value is what has to be refused. Without this it
    // would reach CK_Resources_ResourceType and arrive as a 500.
    [Fact]
    public void Constructor_ThrowsOnUndefinedResourceType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", (ResourceType)99, 8, "America/New_York", false, null, null, null, ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankTimeZoneId(string timeZoneId)
    {
        Assert.Throws<ArgumentException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", ResourceType.Room, 8, timeZoneId, false, null, null, null, ActorId, NowUtc));
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
            new Resource(Guid.NewGuid(), OrgId, "Room A", ResourceType.Room, 8, "America/New_York", false, min, max, null, ActorId, NowUtc));
    }

    [Fact]
    public void Constructor_ThrowsWhenMaxDurationIsBelowMin()
    {
        Assert.Throws<ArgumentException>(() =>
            new Resource(Guid.NewGuid(), OrgId, "Room A", ResourceType.Room, 8, "America/New_York", false, 120, 60, null, ActorId, NowUtc));
    }

    // ---- Edit methods (WP-3 Phase 2 step 1, FR-3.1) ----

    [Fact]
    public void UpdateDetails_ReplacesFieldsAndTouchesAuditFields()
    {
        var resource = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        // A different type from CreateValid's Room, so the assertion below would
        // fail if the field were not actually reassigned.
        resource.UpdateDetails("Board Room", "Top floor", ResourceType.LabSlot, actor, later);

        Assert.Equal("Board Room", resource.Name);
        Assert.Equal("Top floor", resource.Description);
        Assert.Equal(ResourceType.LabSlot, resource.ResourceType);
        Assert.Equal(actor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    // Edits are full representations (docs/decisions/0015), so a null
    // description clears it rather than meaning "leave as is".
    [Fact]
    public void UpdateDetails_ClearsDescriptionWhenNull()
    {
        var resource = CreateValid();

        resource.UpdateDetails("Conference Room A", null, ResourceType.Room, ActorId, NowUtc.AddMinutes(1));

        Assert.Null(resource.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateDetails_ThrowsOnBlankName(string name)
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            resource.UpdateDetails(name, null, ResourceType.Room, ActorId, NowUtc.AddMinutes(1)));
    }

    [Fact]
    public void UpdateDetails_ThrowsOnUndefinedResourceType()
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resource.UpdateDetails("Room A", null, (ResourceType)99, ActorId, NowUtc.AddMinutes(1)));
    }

    // Validation runs before any assignment, so a rejected edit leaves the
    // aggregate exactly as it was rather than half-applied.
    [Fact]
    public void UpdateDetails_LeavesNameUnchangedWhenItThrows()
    {
        var resource = CreateValid();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resource.UpdateDetails("Board Room", null, (ResourceType)99, ActorId, NowUtc.AddMinutes(1)));

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
        var window = resource.AddWindow(
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

    // RemoveApprover, AddAvailabilityWindow and RemoveAvailabilityWindow had a
    // test each here. All three methods were deleted in WP-3 Phase 5 step 4 —
    // the API has gone through ReplaceApprovers and ReplaceAvailabilityWindows
    // exclusively since Phase 3 — so their tests went with them rather than
    // being pointed at a shim. What they asserted is covered by
    // ReplaceApprovers_* below and by AvailabilityWindowTests.

    [Fact]
    public void Archive_SetsIsArchivedTrue()
    {
        var resource = CreateValid();

        resource.Archive(ActorId, NowUtc.AddMinutes(1));

        Assert.True(resource.IsArchived);
    }

    // ---- ReplaceApprovers (WP-3 Phase 3 step 2, FR-3.3) ----

    [Fact]
    public void ReplaceApprovers_ReplacesTheWholeList()
    {
        var resource = CreateValid();
        var original = Guid.NewGuid();
        resource.AddApprover(original, ActorId, NowUtc);

        var replacement = Guid.NewGuid();
        resource.ReplaceApprovers(new[] { replacement }, ActorId, NowUtc.AddMinutes(1));

        // Replace, not merge: the original is gone because it was not resent.
        Assert.Equal(new[] { replacement }, resource.ApproverUserIds);
    }

    [Fact]
    public void ReplaceApprovers_WithAnEmptySet_ClearsTheList()
    {
        var resource = CreateValid();
        resource.AddApprover(Guid.NewGuid(), ActorId, NowUtc);

        resource.ReplaceApprovers(Array.Empty<Guid>(), ActorId, NowUtc.AddMinutes(1));

        Assert.Empty(resource.ApproverUserIds);
    }

    // Set semantics. The Application validator rejects duplicates before they get
    // here, so this is the belt to that braces — the entity still cannot end up
    // holding the same approver twice, which the (ResourceId, UserId) primary key
    // would refuse anyway.
    [Fact]
    public void ReplaceApprovers_CollapsesDuplicates()
    {
        var resource = CreateValid();
        var userId = Guid.NewGuid();

        resource.ReplaceApprovers(new[] { userId, userId }, ActorId, NowUtc);

        Assert.Equal(new[] { userId }, resource.ApproverUserIds);
    }

    [Fact]
    public void ReplaceApprovers_TouchesTheAuditColumns()
    {
        var resource = CreateValid();
        var editor = Guid.NewGuid();
        var later = NowUtc.AddHours(2);

        resource.ReplaceApprovers(new[] { Guid.NewGuid() }, editor, later);

        Assert.Equal(editor, resource.UpdatedByUserId);
        Assert.Equal(later, resource.UpdatedAtUtc);
    }

    // The entity is allowed to hold this state: RequiresApproval with no
    // approvers is refused by the Application layer, which is the only place that
    // can attach a reason code to the refusal.
    [Fact]
    public void ReplaceApprovers_DoesNotEnforceTheRequiresApprovalInvariant()
    {
        var resource = CreateValid();
        resource.SetRequiresApproval(true, ActorId, NowUtc);

        resource.ReplaceApprovers(Array.Empty<Guid>(), ActorId, NowUtc.AddMinutes(1));

        Assert.True(resource.RequiresApproval);
        Assert.Empty(resource.ApproverUserIds);
    }

    // ---- The duration limits, as questions (2026-09-04) ----
    //
    // Two questions, and the asymmetry between them is the whole reason they are
    // separate methods. Both live here so the availability query's filter and
    // WP-4's booking-length rejection cannot drift apart: until now the minimum
    // was read in one place and the maximum in none at all.

    // Read side. A span longer than the maximum is not a problem — a booker takes
    // a piece of it — so only the minimum bears on whether a span is worth
    // offering.
    [Theory]
    [InlineData(30, 29, false)]
    [InlineData(30, 30, true)]
    [InlineData(30, 31, true)]
    [InlineData(30, 600, true)]  // Far beyond the 240-minute maximum, and fine.
    public void CanFitABooking_ConsidersOnlyTheMinimum(int min, int spanMinutes, bool expected)
    {
        var resource = CreateValid();
        resource.SetDurationLimits(min, 240, ActorId, NowUtc);

        Assert.Equal(expected, resource.CanFitABooking(TimeSpan.FromMinutes(spanMinutes)));
    }

    [Fact]
    public void CanFitABooking_WithNoMinimum_AcceptsAnySpan()
    {
        var resource = CreateValid();
        resource.SetDurationLimits(null, null, ActorId, NowUtc);

        Assert.True(resource.CanFitABooking(TimeSpan.FromMinutes(1)));
    }

    // Write side. Both bounds apply to the length of an actual booking.
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(240, true)]
    [InlineData(241, false)]
    public void AllowsBookingDuration_ConsidersBothBounds(int durationMinutes, bool expected)
    {
        var resource = CreateValid();
        resource.SetDurationLimits(30, 240, ActorId, NowUtc);

        Assert.Equal(expected, resource.AllowsBookingDuration(TimeSpan.FromMinutes(durationMinutes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void AllowsBookingDuration_RefusesAnEmptyOrNegativeDuration(int durationMinutes)
    {
        var resource = CreateValid();
        resource.SetDurationLimits(null, null, ActorId, NowUtc);

        Assert.False(resource.AllowsBookingDuration(TimeSpan.FromMinutes(durationMinutes)));
    }

    [Fact]
    public void AllowsBookingDuration_WithNoLimits_AcceptsAnyPositiveDuration()
    {
        var resource = CreateValid();
        resource.SetDurationLimits(null, null, ActorId, NowUtc);

        Assert.True(resource.AllowsBookingDuration(TimeSpan.FromDays(1)));
    }

    // Either bound alone, since both are independently nullable.
    [Fact]
    public void AllowsBookingDuration_WithOnlyAMaximum_IgnoresTheAbsentMinimum()
    {
        var resource = CreateValid();
        resource.SetDurationLimits(null, 60, ActorId, NowUtc);

        Assert.True(resource.AllowsBookingDuration(TimeSpan.FromMinutes(5)));
        Assert.False(resource.AllowsBookingDuration(TimeSpan.FromMinutes(61)));
    }

    [Fact]
    public void AllowsBookingDuration_WithOnlyAMinimum_IgnoresTheAbsentMaximum()
    {
        var resource = CreateValid();
        resource.SetDurationLimits(60, null, ActorId, NowUtc);

        Assert.False(resource.AllowsBookingDuration(TimeSpan.FromMinutes(59)));
        Assert.True(resource.AllowsBookingDuration(TimeSpan.FromDays(1)));
    }
}
