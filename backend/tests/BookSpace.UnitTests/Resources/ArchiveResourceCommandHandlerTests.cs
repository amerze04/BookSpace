using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.ArchiveResource;
using BookSpace.Domain.Entities;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 2 step 4, FR-3.5.
public class ArchiveResourceCommandHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid ArchiverId = Guid.NewGuid();
    private static readonly DateTime CreatedUtc = new(2026, 8, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private static Resource ExistingResource() =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", "Room", 8, "America/New_York",
            requiresApproval: false, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: CreatedUtc);

    private static ArchiveResourceCommandHandler Handler(FakeResourceRepository repository) =>
        new(repository, new FixedCurrentUser(ArchiverId), new TestClock(NowUtc));

    [Fact]
    public async Task Handle_ArchivesTheResourceAndTouchesTheAuditColumns()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            new ArchiveResourceCommand(resource.Id), CancellationToken.None);

        Assert.True(response.IsArchived);
        Assert.True(resource.IsArchived);
        Assert.Equal(ArchiverId, resource.UpdatedByUserId);
        Assert.Equal(NowUtc, resource.UpdatedAtUtc);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    // FR-3.5: archiving preserves the resource, so everything but the flag is
    // untouched. Nothing is deleted (CLAUDE.md §4.5).
    [Fact]
    public async Task Handle_LeavesEveryOtherFieldAlone()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            new ArchiveResourceCommand(resource.Id), CancellationToken.None);

        Assert.Equal("Conference Room A", response.Name);
        Assert.Equal(8, response.Capacity);
        Assert.Equal("America/New_York", response.TimeZoneId);
        Assert.Equal(CreatedUtc, response.CreatedAtUtc);
    }

    [Fact]
    public async Task Handle_UnknownResource_ThrowsResourceNotFound()
    {
        var repository = new FakeResourceRepository(ExistingResource());

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(repository).Handle(new ArchiveResourceCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    // Idempotent on purpose: a transition to a terminal state that already holds
    // is the outcome the caller asked for, not a rule violation — and a retry
    // after a dropped response has to be safe. Contrast
    // UpdateResourceCommandHandlerTests, where editing an archived resource is
    // refused with ResourceArchived.
    [Fact]
    public async Task Handle_AlreadyArchived_SucceedsWithoutSavingAgain()
    {
        var resource = ExistingResource();
        resource.Archive(ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository).Handle(
            new ArchiveResourceCommand(resource.Id), CancellationToken.None);

        Assert.True(response.IsArchived);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // A no-op must not move UpdatedAtUtc, or "last changed" starts meaning
    // "last asked about".
    [Fact]
    public async Task Handle_AlreadyArchived_DoesNotTouchTheAuditColumns()
    {
        var resource = ExistingResource();
        resource.Archive(ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        await Handler(repository).Handle(new ArchiveResourceCommand(resource.Id), CancellationToken.None);

        Assert.Equal(ActorId, resource.UpdatedByUserId);
        Assert.Equal(CreatedUtc, resource.UpdatedAtUtc);
    }

    [Fact]
    public async Task Handle_WithNoAuthenticatedUser_ThrowsInvalidOperation()
    {
        var resource = ExistingResource();
        var handler = new ArchiveResourceCommandHandler(
            new FakeResourceRepository(resource),
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ArchiveResourceCommand(resource.Id), CancellationToken.None));
    }
}
