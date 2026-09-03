using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.ReplaceApprovers;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 3 step 2, FR-3.3.
public class ReplaceApproversCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid EditorId = Guid.NewGuid();
    private static readonly Guid EligibleOne = Guid.NewGuid();
    private static readonly Guid EligibleTwo = Guid.NewGuid();
    private static readonly Guid Ineligible = Guid.NewGuid();
    private static readonly DateTime CreatedUtc = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Resource ExistingResource(bool requiresApproval = false) =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, 8, "America/New_York",
            requiresApproval, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: CreatedUtc);

    private static ReplaceApproversCommandRequestHandler Handler(
        FakeResourceRepository resources,
        FakeUserRepository users) =>
        new(resources, users, new FixedCurrentUser(EditorId), new TestClock(NowUtc));

    private static FakeUserRepository EligibleUsers() => new(EligibleOne, EligibleTwo);

    [Fact]
    public async Task Handle_AssignsTheApproversAndSaves()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository, EligibleUsers()).Handle(
            new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleOne, EligibleTwo }),
            CancellationToken.None);

        Assert.Equal(resource.Id, response.ResourceId);
        Assert.Equal(2, response.Approvers.Count);
        Assert.Equal(new[] { EligibleOne, EligibleTwo }, resource.ApproverUserIds);
        Assert.Equal(1, repository.SaveChangesCount);

        // Names, not bare Guids — the whole reason the response is not a 204.
        Assert.All(response.Approvers, a => Assert.False(string.IsNullOrWhiteSpace(a.FullName)));
    }

    // Replace, not merge.
    [Fact]
    public async Task Handle_DiscardsThePreviousApprovers()
    {
        var resource = ExistingResource();
        resource.AddApprover(EligibleTwo, ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        await Handler(repository, EligibleUsers()).Handle(
            new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleOne }),
            CancellationToken.None);

        Assert.Equal(new[] { EligibleOne }, resource.ApproverUserIds);
    }

    [Fact]
    public async Task Handle_TouchesTheResourceAuditColumns()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);

        await Handler(repository, EligibleUsers()).Handle(
            new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleOne }),
            CancellationToken.None);

        Assert.Equal(EditorId, resource.UpdatedByUserId);
        Assert.Equal(NowUtc, resource.UpdatedAtUtc);
    }

    // A resource that does not require approval may have none. Legitimate, and
    // the only way to take a resource out of the approval flow after clearing the
    // flag on the resource edit.
    [Fact]
    public async Task Handle_EmptyList_OnAResourceThatDoesNotRequireApproval_ClearsTheList()
    {
        var resource = ExistingResource(requiresApproval: false);
        resource.AddApprover(EligibleOne, ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);
        var users = EligibleUsers();

        var response = await Handler(repository, users).Handle(
            new ReplaceApproversCommandRequest(resource.Id, Array.Empty<Guid>()),
            CancellationToken.None);

        Assert.Empty(response.Approvers);
        Assert.Empty(resource.ApproverUserIds);
        Assert.Equal(1, repository.SaveChangesCount);

        // Nothing to look up, so the eligibility query is skipped entirely.
        Assert.False(users.EligibilityWasQueried);
    }

    // The other half of the invariant Phase 2 built. Without this an admin could
    // set RequiresApproval with an approver assigned, then empty the list here,
    // and land in exactly the state FR-3.3 rules out.
    [Fact]
    public async Task Handle_EmptyList_OnAResourceThatRequiresApproval_ThrowsApproversRequired()
    {
        var resource = ExistingResource(requiresApproval: true);
        resource.AddApprover(EligibleOne, ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<ApproversRequiredException>(() =>
            Handler(repository, EligibleUsers()).Handle(
                new ReplaceApproversCommandRequest(resource.Id, Array.Empty<Guid>()),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.ApproversRequired, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);

        // And the existing approver survived — rule checks run before any mutator.
        Assert.Equal(new[] { EligibleOne }, resource.ApproverUserIds);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task Handle_IneligibleApprover_ThrowsApproverNotEligibleAndChangesNothing()
    {
        var resource = ExistingResource();
        resource.AddApprover(EligibleOne, ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<ApproverNotEligibleException>(() =>
            Handler(repository, EligibleUsers()).Handle(
                new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleTwo, Ineligible }),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.ApproverNotEligible, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);

        Assert.Equal(new[] { EligibleOne }, resource.ApproverUserIds);
        Assert.Equal(CreatedUtc, resource.UpdatedAtUtc);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // One request names every offending id, so an admin fixing a list does not
    // discover the ineligible members one round trip at a time.
    [Fact]
    public async Task Handle_ReportsEveryIneligibleApproverAtOnce()
    {
        var resource = ExistingResource();
        var repository = new FakeResourceRepository(resource);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ApproverNotEligibleException>(() =>
            Handler(repository, EligibleUsers()).Handle(
                new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleOne, first, second }),
                CancellationToken.None));

        // The message is log-only (docs/decisions/0016), which is exactly why it
        // is worth asserting here: nothing downstream can.
        Assert.Contains(first.ToString(), exception.Message);
        Assert.Contains(second.ToString(), exception.Message);
        Assert.DoesNotContain(EligibleOne.ToString(), exception.Message);
    }

    // Another tenant's real id arrives as null from the tenant-filtered
    // repository, indistinguishable from an id that exists nowhere (AC-4).
    [Fact]
    public async Task Handle_UnknownResource_ThrowsResourceNotFound()
    {
        var repository = new FakeResourceRepository(ExistingResource());

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(repository, EligibleUsers()).Handle(
                new ReplaceApproversCommandRequest(Guid.NewGuid(), new[] { EligibleOne }),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task Handle_ArchivedResource_ThrowsResourceArchived()
    {
        var resource = ExistingResource();
        resource.Archive(ActorId, CreatedUtc);
        var repository = new FakeResourceRepository(resource);

        var exception = await Assert.ThrowsAsync<ResourceArchivedException>(() =>
            Handler(repository, EligibleUsers()).Handle(
                new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleOne }),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceArchived, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // The flag comes from the resource, never the payload: this endpoint does not
    // set it, and echoing it is what lets a client understand an ApproversRequired
    // refusal without a second read.
    [Fact]
    public async Task Handle_EchoesRequiresApprovalFromTheResource()
    {
        var resource = ExistingResource(requiresApproval: true);
        var repository = new FakeResourceRepository(resource);

        var response = await Handler(repository, EligibleUsers()).Handle(
            new ReplaceApproversCommandRequest(resource.Id, new[] { EligibleOne }),
            CancellationToken.None);

        Assert.True(response.RequiresApproval);
    }
}
