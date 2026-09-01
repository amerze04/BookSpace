using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.UpdateResource;
using BookSpace.Domain.Entities;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 2 step 3, FR-3.1 edits and FR-3.5's refusal to edit an archived
// resource.
public class UpdateResourceCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid EditorId = Guid.NewGuid();
    private static readonly DateTime CreatedUtc = new(2026, 8, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private const string KnownZone = "America/New_York";
    private const string OtherKnownZone = "Europe/Zagreb";

    private static Resource ExistingResource(int capacity = 8, bool requiresApproval = false) =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", "Room", capacity, KnownZone,
            requiresApproval, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: CreatedUtc);

    private static UpdateResourceCommandRequest Command(
        Guid resourceId,
        string name = "Board Room",
        string? description = "Top floor",
        int capacity = 8,
        string timeZoneId = KnownZone,
        bool requiresApproval = false,
        int? min = 30,
        int? max = 240) =>
        new(resourceId, name, description, "MeetingRoom", capacity, timeZoneId, requiresApproval, min, max);

    private static UpdateResourceCommandRequestHandler Handler(FakeResourceRepository repository) =>
        new(
            repository,
            new FakeTimeZoneCatalog(KnownZone, OtherKnownZone),
            new FixedCurrentUser(EditorId),
            new TestClock(NowUtc));

    [Fact]
    public async Task Handle_AppliesEveryFieldAndTouchesTheAuditColumns()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            Command(resource.Id, capacity: 12, min: 15, max: 60), CancellationToken.None);

        Assert.Equal(1, repository.SaveChangesCount);
        Assert.Equal("Board Room", response.Name);
        Assert.Equal("Top floor", response.Description);
        Assert.Equal("MeetingRoom", response.ResourceType);
        Assert.Equal(12, response.Capacity);
        Assert.Equal(15, response.MinDurationMinutes);
        Assert.Equal(60, response.MaxDurationMinutes);
        Assert.Equal(EditorId, resource.UpdatedByUserId);
        Assert.Equal(NowUtc, resource.UpdatedAtUtc);
        // Creation audit is untouched by an edit.
        Assert.Equal(ActorId, resource.CreatedByUserId);
        Assert.Equal(CreatedUtc, resource.CreatedAtUtc);
    }

    // Full representation, not a patch (docs/decisions/0015).
    [Fact]
    public async Task Handle_NullsClearRatherThanPreserve()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            Command(resource.Id, description: null, min: null, max: null), CancellationToken.None);

        Assert.Null(response.Description);
        Assert.Null(response.MinDurationMinutes);
        Assert.Null(response.MaxDurationMinutes);
    }

    [Fact]
    public async Task Handle_UnknownResource_ThrowsResourceNotFound()
    {
        var repository = new FakeResourceRepository(ExistingResource());

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(repository).Handle(Command(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    // FR-3.5. The resource stays readable but takes no further edits.
    [Fact]
    public async Task Handle_ArchivedResource_ThrowsResourceArchived()
    {
        var resource = ExistingResource();
        resource.Archive(ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<ResourceArchivedException>(() =>
            Handler(repository).Handle(Command(resource.Id), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceArchived, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(0, repository.SaveChangesCount);
        // Nothing was applied before the refusal.
        Assert.Equal("Conference Room A", resource.Name);
    }

    [Fact]
    public async Task Handle_UnknownTimeZone_ThrowsInvalidTimeZone()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<InvalidTimeZoneIdException>(() =>
            Handler(repository).Handle(
                Command(resource.Id, timeZoneId: "Eastern Standard Time"), CancellationToken.None));

        Assert.Equal(ReasonCodes.InvalidTimeZone, exception.ReasonCode);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task Handle_RequiresApprovalWithNoApprovers_ThrowsApproversRequired()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<ApproversRequiredException>(() =>
            Handler(repository).Handle(
                Command(resource.Id, requiresApproval: true), CancellationToken.None));

        Assert.Equal(ReasonCodes.ApproversRequired, exception.ReasonCode);
        Assert.False(resource.RequiresApproval);
    }

    // The same edit succeeds once an approver exists — which is what makes the
    // flag reachable at all before Phase 3 adds the assignment endpoint.
    [Fact]
    public async Task Handle_RequiresApprovalWithAnApprover_Succeeds()
    {
        var resource = ExistingResource();
        resource.AddApprover(Guid.NewGuid(), ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            Command(resource.Id, requiresApproval: true), CancellationToken.None);

        Assert.True(response.RequiresApproval);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    // ---- Capacity vs existing bookings (wp3-plan's "smaller calls") ----

    [Fact]
    public async Task Handle_CapacityDecreaseBelowCommittedUnits_ThrowsCapacityBelowExistingBookings()
    {
        var resource = ExistingResource(capacity: 8);
        var repository = new FakeResourceRepository(resource, peakConcurrentBookedQuantity: 5);

        var exception = await Assert.ThrowsAsync<CapacityBelowExistingBookingsException>(() =>
            Handler(repository).Handle(Command(resource.Id, capacity: 4), CancellationToken.None));

        Assert.Equal(ReasonCodes.CapacityBelowExistingBookings, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(8, resource.Capacity);
    }

    // Exactly enough is enough: the rule is "below", not "at or below".
    [Fact]
    public async Task Handle_CapacityDecreaseToExactlyTheCommittedUnits_Succeeds()
    {
        var resource = ExistingResource(capacity: 8);
        var repository = new FakeResourceRepository(resource, peakConcurrentBookedQuantity: 5);

        var response = await Handler(repository).Handle(
            Command(resource.Id, capacity: 5), CancellationToken.None);

        Assert.Equal(5, response.Capacity);
    }

    // An increase cannot strand a booking, so the query is skipped entirely.
    [Fact]
    public async Task Handle_CapacityIncrease_DoesNotQueryExistingBookings()
    {
        var resource = ExistingResource(capacity: 8);
        var repository = new FakeResourceRepository(resource, peakConcurrentBookedQuantity: 99);

        var response = await Handler(repository).Handle(
            Command(resource.Id, capacity: 20), CancellationToken.None);

        Assert.Equal(20, response.Capacity);
        Assert.False(repository.PeakWasQueried);
    }

    [Fact]
    public async Task Handle_UnchangedCapacity_DoesNotQueryExistingBookings()
    {
        var resource = ExistingResource(capacity: 8);
        var repository = new FakeResourceRepository(resource, peakConcurrentBookedQuantity: 99);

        await Handler(repository).Handle(Command(resource.Id, capacity: 8), CancellationToken.None);

        Assert.False(repository.PeakWasQueried);
    }

    // ---- The timezone-change notice ----

    [Fact]
    public async Task Handle_UnchangedTimeZone_ReportsNoNotice()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            Command(resource.Id, timeZoneId: KnownZone), CancellationToken.None);

        Assert.Null(response.TimeZoneChange);
    }

    // wp3-plan's "smaller calls": the windows are not rewritten, they are
    // reinterpreted — so the response says so rather than letting the admin find
    // out from a booking that lands an hour off.
    [Fact]
    public async Task Handle_ChangedTimeZone_ReportsTheReinterpretedWindowCount()
    {
        var resource = ExistingResource();
        resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, CreatedUtc);
        resource.AddAvailabilityWindow(
            Guid.NewGuid(), DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            Command(resource.Id, timeZoneId: OtherKnownZone), CancellationToken.None);

        var notice = Assert.IsType<TimeZoneChangeNotice>(response.TimeZoneChange);
        Assert.Equal(KnownZone, notice.PreviousTimeZoneId);
        Assert.Equal(OtherKnownZone, notice.NewTimeZoneId);
        Assert.Equal(2, notice.ReinterpretedAvailabilityWindowCount);

        // The windows themselves are untouched — that is the whole point.
        Assert.All(resource.AvailabilityWindows, w => Assert.Equal(new TimeOnly(9, 0), w.OpensAt));
    }

    [Fact]
    public async Task Handle_ChangedTimeZoneWithNoWindows_StillReportsTheChange()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            Command(resource.Id, timeZoneId: OtherKnownZone), CancellationToken.None);

        Assert.NotNull(response.TimeZoneChange);
        Assert.Equal(0, response.TimeZoneChange!.ReinterpretedAvailabilityWindowCount);
    }
}
