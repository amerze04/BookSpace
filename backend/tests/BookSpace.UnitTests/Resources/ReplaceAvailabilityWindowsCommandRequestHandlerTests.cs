using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;
using BookSpace.Domain.Entities;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 3, FR-3.2.
public class ReplaceAvailabilityWindowsCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid EditorId = Guid.NewGuid();
    private static readonly DateTime CreatedUtc = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Resource ExistingResource() =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", "Room", 8, "America/New_York",
            requiresApproval: false, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: CreatedUtc);

    private static ReplaceAvailabilityWindowsCommandRequestHandler Handler(FakeResourceRepository repository) =>
        new(repository, new FixedCurrentUser(EditorId), new TestClock(NowUtc));

    private static AvailabilityWindowCommandItem Item(DayOfWeek weekday, int opensHour, int closesHour) =>
        new(weekday, new TimeOnly(opensHour, 0), new TimeOnly(closesHour, 0));

    [Fact]
    public async Task Handle_ReplacesTheScheduleAndSaves()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            new ReplaceAvailabilityWindowsCommandRequest(
                resource.Id,
                new[] { Item(DayOfWeek.Monday, 9, 17), Item(DayOfWeek.Tuesday, 9, 12) }),
            CancellationToken.None);

        Assert.Equal(resource.Id, response.ResourceId);
        Assert.Equal(2, response.AvailabilityWindows.Count);
        Assert.Equal(2, resource.AvailabilityWindows.Count);
        Assert.Equal(1, repository.SaveChangesCount);

        // Ids are minted server-side; the request items carry none.
        Assert.All(response.AvailabilityWindows, w => Assert.NotEqual(Guid.Empty, w.Id));

        // Stated as inserts, not left for EF to infer from the navigation. Worth
        // asserting here and not only in the integration suite: EF marks a window
        // reached through the navigation as Modified because its key is already
        // set, and the resulting zero-row UPDATE surfaces as a 409 that looks
        // nothing like the missing Add that caused it.
        Assert.Equal(2, repository.AddedAvailabilityWindows.Count);
        Assert.Equal(
            resource.AvailabilityWindows.Select(w => w.Id).OrderBy(id => id),
            response.AvailabilityWindows.Select(w => w.Id).OrderBy(id => id));
    }

    // A weekly schedule has an obvious reading order, and the read detail sorts
    // the same way — so a client never has to sort either response.
    [Fact]
    public async Task Handle_ReturnsTheScheduleOrderedByWeekdayThenOpeningTime()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            new ReplaceAvailabilityWindowsCommandRequest(
                resource.Id,
                new[]
                {
                    Item(DayOfWeek.Friday, 9, 12),
                    Item(DayOfWeek.Monday, 13, 17),
                    Item(DayOfWeek.Monday, 9, 12),
                }),
            CancellationToken.None);

        Assert.Equal(
            new[]
            {
                (DayOfWeek.Monday, new TimeOnly(9, 0)),
                (DayOfWeek.Monday, new TimeOnly(13, 0)),
                (DayOfWeek.Friday, new TimeOnly(9, 0)),
            },
            response.AvailabilityWindows.Select(w => (w.Weekday, w.OpensAt)));
    }

    [Fact]
    public async Task Handle_WithAnEmptySet_ClearsTheSchedule()
    {
        var resource = ExistingResource();
        resource.AddWindow(
            Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            new ReplaceAvailabilityWindowsCommandRequest(
                resource.Id, Array.Empty<AvailabilityWindowCommandItem>()),
            CancellationToken.None);

        Assert.Empty(response.AvailabilityWindows);
        Assert.Empty(resource.AvailabilityWindows);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task Handle_TouchesTheResourceAuditColumns()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        await Handler(repository).Handle(
            new ReplaceAvailabilityWindowsCommandRequest(
                resource.Id, new[] { Item(DayOfWeek.Monday, 9, 17) }),
            CancellationToken.None);

        // The windows have no audit columns, so the resource's are the only
        // record that the schedule changed at all.
        Assert.Equal(EditorId, resource.UpdatedByUserId);
        Assert.Equal(NowUtc, resource.UpdatedAtUtc);
    }

    // Another tenant's real id arrives as null from the tenant-filtered
    // repository, so it is indistinguishable from an id that exists nowhere (AC-4).
    [Fact]
    public async Task Handle_UnknownResource_ThrowsResourceNotFound()
    {
        var repository = new FakeResourceRepository(ExistingResource());

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(repository).Handle(
                new ReplaceAvailabilityWindowsCommandRequest(
                    Guid.NewGuid(), new[] { Item(DayOfWeek.Monday, 9, 17) }),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    // FR-3.5: a schedule is only meaningful for something bookable.
    [Fact]
    public async Task Handle_ArchivedResource_ThrowsResourceArchived()
    {
        var resource = ExistingResource();
        resource.Archive(ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<ResourceArchivedException>(() =>
            Handler(repository).Handle(
                new ReplaceAvailabilityWindowsCommandRequest(
                    resource.Id, new[] { Item(DayOfWeek.Monday, 9, 17) }),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceArchived, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task Handle_OverlappingWindows_ThrowsAndLeavesTheExistingScheduleUntouched()
    {
        var resource = ExistingResource();
        var originalId = Guid.NewGuid();
        resource.AddWindow(
            originalId, DayOfWeek.Thursday, new TimeOnly(9, 0), new TimeOnly(17, 0), ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<OverlappingAvailabilityWindowException>(() =>
            Handler(repository).Handle(
                new ReplaceAvailabilityWindowsCommandRequest(
                    resource.Id,
                    new[] { Item(DayOfWeek.Monday, 9, 13), Item(DayOfWeek.Monday, 12, 17) }),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.OverlappingAvailabilityWindow, exception.ReasonCode);
        Assert.Equal(ErrorKind.Conflict, exception.Kind);

        // Rule checks run before any mutator, so the rejected request left the
        // tracked aggregate exactly as it found it — which matters in a
        // request-scoped DbContext.
        var untouched = Assert.Single(resource.AvailabilityWindows);
        Assert.Equal(originalId, untouched.Id);
        Assert.Equal(CreatedUtc, resource.UpdatedAtUtc);
        Assert.Equal(0, repository.SaveChangesCount);
    }
}
