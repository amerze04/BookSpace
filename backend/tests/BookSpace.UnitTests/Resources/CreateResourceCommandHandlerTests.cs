using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.UnitTests.Persistence;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 2 step 3, FR-3.1. The rule checks and where the audit values come
// from; the HTTP contract around them is in the integration suite.
public class CreateResourceCommandHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private const string KnownZone = "America/New_York";

    private static CreateResourceCommand ValidCommand(
        bool requiresApproval = false,
        string timeZoneId = KnownZone) =>
        new("Conference Room B", "Second floor", "Room", 6, timeZoneId, requiresApproval, 30, 240);

    private static CreateResourceCommandHandler Handler(
        FakeResourceRepository repository,
        Guid? orgId = null,
        Guid? actorId = null) =>
        new(
            repository,
            new FakeTimeZoneCatalog(KnownZone),
            new FixedCurrentTenant(orgId ?? OrgId),
            new FixedCurrentUser(actorId ?? ActorId),
            new TestClock(NowUtc));

    [Fact]
    public async Task Handle_CreatesTheResourceAndReturnsItsDetail()
    {
        var repository = new FakeResourceRepository();

        var response = await Handler(repository).Handle(ValidCommand(), CancellationToken.None);

        var created = Assert.IsType<Domain.Entities.Resource>(repository.Added);
        Assert.Equal(1, repository.SaveChangesCount);
        Assert.Equal(created.Id, response.Id);
        Assert.Equal("Conference Room B", response.Name);
        Assert.Equal("Second floor", response.Description);
        Assert.Equal("Room", response.ResourceType);
        Assert.Equal(6, response.Capacity);
        Assert.Equal(KnownZone, response.TimeZoneId);
        Assert.Equal(30, response.MinDurationMinutes);
        Assert.Equal(240, response.MaxDurationMinutes);
        Assert.False(response.IsArchived);
    }

    // OrgId and CreatedByUserId come from the token, never the payload — the
    // command has no field for either.
    [Fact]
    public async Task Handle_TakesOrgAndActorFromTheAmbientContext()
    {
        var repository = new FakeResourceRepository();

        await Handler(repository).Handle(ValidCommand(), CancellationToken.None);

        Assert.Equal(OrgId, repository.Added!.OrgId);
        Assert.Equal(ActorId, repository.Added.CreatedByUserId);
        Assert.Equal(NowUtc, repository.Added.CreatedAtUtc);
        Assert.Equal(NowUtc, repository.Added.UpdatedAtUtc);
    }

    [Fact]
    public async Task Handle_UnknownTimeZone_ThrowsInvalidTimeZone()
    {
        var repository = new FakeResourceRepository();

        var exception = await Assert.ThrowsAsync<InvalidTimeZoneIdException>(() =>
            Handler(repository).Handle(ValidCommand(timeZoneId: "Mars/Olympus"), CancellationToken.None));

        Assert.Equal(ReasonCodes.InvalidTimeZone, exception.ReasonCode);
        Assert.Equal(ErrorKind.Validation, exception.Kind);
        Assert.Null(repository.Added);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // FR-3.3: a resource cannot be created already requiring approval, because
    // there is no way to give it an approver in the same request (Phase 3 owns
    // assignment). Refused rather than saved into a state FR-3.3 rules out.
    [Fact]
    public async Task Handle_RequiresApprovalWithNoApprovers_ThrowsApproversRequired()
    {
        var repository = new FakeResourceRepository();

        var exception = await Assert.ThrowsAsync<ApproversRequiredException>(() =>
            Handler(repository).Handle(ValidCommand(requiresApproval: true), CancellationToken.None));

        Assert.Equal(ReasonCodes.ApproversRequired, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Null(repository.Added);
    }

    // A 500, not a reason code: these endpoints sit behind TenantAdmin plus
    // TenantMember's orgId-claim requirement, so a missing tenant or subject is
    // broken wiring rather than something a client did.
    [Fact]
    public async Task Handle_WithNoTenantContext_ThrowsInvalidOperation()
    {
        var repository = new FakeResourceRepository();
        var handler = new CreateResourceCommandHandler(
            repository,
            new FakeTimeZoneCatalog(KnownZone),
            new FixedCurrentTenant(null),
            new FixedCurrentUser(ActorId),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(ValidCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WithNoAuthenticatedUser_ThrowsInvalidOperation()
    {
        var repository = new FakeResourceRepository();
        var handler = new CreateResourceCommandHandler(
            repository,
            new FakeTimeZoneCatalog(KnownZone),
            new FixedCurrentTenant(OrgId),
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(ValidCommand(), CancellationToken.None));
    }
}
