using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Users.DeactivateUser;
using BookSpace.Application.Features.Users.ReactivateUser;
using BookSpace.Application.Features.Users.ReplaceUserRoles;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Users;

// The three phase-5 writes, and mostly the rule they share: a tenant must keep
// at least one active TenantAdmin.
//
// What these cannot prove is that the guard's read takes a lock — a fake has no
// transaction to hold one in. That is UserWriteEndpointTests' job, against real
// SQL Server, with two requests racing. What they *can* prove, and what a
// concurrency test would not, is the decision table: which combinations of
// current state and requested change the guard fires on, and which it must not.
public class UserWriteHandlerTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 24, 13, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly FakeUserRepository _users = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly TestClock _clock = new(NowUtc);

    // ---- Deactivate ----

    [Fact]
    public async Task Deactivate_SetsTheFlagAndRecordsTheActor()
    {
        var admin = Admin();
        var member = Member();

        var response = await Deactivate(member.Id);

        Assert.False(response.IsActive);
        Assert.False(member.IsActive);
        Assert.Equal(ActorId, member.UpdatedByUserId);
        Assert.Equal(NowUtc, member.UpdatedAtUtc);
        Assert.True(admin.IsActive);
    }

    [Fact]
    public async Task Deactivate_AnUnknownUserIsNotFound()
    {
        Admin();

        var exception = await Assert.ThrowsAsync<UserNotFoundException>(() => Deactivate(Guid.NewGuid()));

        Assert.Equal(ReasonCodes.UserNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    // Idempotent, and the early return is what keeps it honest: a no-op must not
    // move UpdatedAtUtc, or "last changed" starts meaning "last asked about".
    [Fact]
    public async Task Deactivate_AnAlreadyInactiveUserWritesNothing()
    {
        Admin();
        var member = Member();
        member.Deactivate(ActorId, NowUtc.AddDays(-1));

        var response = await Deactivate(member.Id);

        Assert.False(response.IsActive);
        Assert.Equal(NowUtc.AddDays(-1), member.UpdatedAtUtc);
        Assert.Equal(0, _users.SaveCount);
    }

    // And it does not even take the lock — there is nothing a second
    // deactivation of an inactive user could empty.
    [Fact]
    public async Task Deactivate_AnAlreadyInactiveUserDoesNotConsultTheGuard()
    {
        Admin();
        var member = Member();
        member.Deactivate(ActorId, NowUtc.AddDays(-1));

        await Deactivate(member.Id);

        Assert.Equal(0, _users.LastAdminCountQueries);
    }

    // **The invariant.**
    [Fact]
    public async Task Deactivate_TheLastActiveAdminIsRefused()
    {
        var admin = Admin();

        var exception = await Assert.ThrowsAsync<LastTenantAdminException>(() => Deactivate(admin.Id));

        Assert.Equal(ReasonCodes.LastTenantAdmin, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.True(admin.IsActive);
        Assert.Equal(0, _users.SaveCount);
    }

    [Fact]
    public async Task Deactivate_AnAdminIsAllowedWhenAnotherActiveOneRemains()
    {
        var first = Admin();
        Admin();

        await Deactivate(first.Id);

        Assert.False(first.IsActive);
    }

    // The *other* admin has to be active to count. Two admins, one already
    // deactivated, is a tenant with one — so deactivating the survivor is
    // refused.
    [Fact]
    public async Task Deactivate_AnInactiveAdminDoesNotCountTowardsTheMinimum()
    {
        var survivor = Admin();
        var departed = Admin();
        departed.Deactivate(ActorId, NowUtc.AddDays(-1));

        await Assert.ThrowsAsync<LastTenantAdminException>(() => Deactivate(survivor.Id));
    }

    // An Approver is not an administrator, however close the policy sits.
    [Fact]
    public async Task Deactivate_AnApproverDoesNotCountTowardsTheMinimum()
    {
        var admin = Admin();
        User(Role.Approver);

        await Assert.ThrowsAsync<LastTenantAdminException>(() => Deactivate(admin.Id));
    }

    // Nothing stops an admin deactivating themselves while another remains. The
    // guard is about the tenant keeping an administrator, not about who is
    // holding the mouse.
    [Fact]
    public async Task Deactivate_AnAdminMayDeactivateThemselvesWhenAnotherRemains()
    {
        var self = Admin();
        Admin();

        var response = await Deactivate(self.Id, actorUserId: self.Id);

        Assert.False(response.IsActive);
    }

    // The guard is read-check-write across two rows, so the check and the write
    // have to share a transaction — otherwise the lock is released before the
    // write and two admins can still race. See the handler.
    [Fact]
    public async Task Deactivate_RunsInsideAUnitOfWork()
    {
        Admin();
        var member = Member();

        await Deactivate(member.Id);

        Assert.Equal(1, _unitOfWork.Executions);
    }

    // ---- Reactivate ----

    [Fact]
    public async Task Reactivate_ClearsTheFlag()
    {
        var member = Member();
        member.Deactivate(ActorId, NowUtc.AddDays(-1));

        var response = await Reactivate(member.Id);

        Assert.True(response.IsActive);
        Assert.Equal(NowUtc, member.UpdatedAtUtc);
    }

    [Fact]
    public async Task Reactivate_AnAlreadyActiveUserWritesNothing()
    {
        var member = Member();

        var response = await Reactivate(member.Id);

        Assert.True(response.IsActive);
        Assert.Equal(0, _users.SaveCount);
    }

    [Fact]
    public async Task Reactivate_AnUnknownUserIsNotFound()
    {
        await Assert.ThrowsAsync<UserNotFoundException>(() => Reactivate(Guid.NewGuid()));
    }

    // Reactivating can only grow the set of active administrators, so there is
    // nothing to serialize against and no transaction is opened. Asserted so a
    // later edit that wraps it has to be deliberate.
    [Fact]
    public async Task Reactivate_TakesNoLockAndOpensNoTransaction()
    {
        var member = Member();
        member.Deactivate(ActorId, NowUtc.AddDays(-1));

        await Reactivate(member.Id);

        Assert.Equal(0, _users.LastAdminCountQueries);
        Assert.Equal(0, _unitOfWork.Executions);
    }

    // ---- Roles ----

    [Fact]
    public async Task ReplaceRoles_StoresTheRequestedSet()
    {
        Admin();
        var member = Member();

        var response = await ReplaceRoles(member.Id, [Role.Approver, Role.Member]);

        Assert.Equal([Role.Approver, Role.Member], response.Roles);
        Assert.Equal(ActorId, member.UpdatedByUserId);
    }

    // Ordered by name in the response, because the owned collection's order is
    // whatever the change tracker produced — two identical sets must not
    // serialize differently.
    [Fact]
    public async Task ReplaceRoles_ReturnsTheSetInAStableOrder()
    {
        Admin();
        var member = Member();

        var first = await ReplaceRoles(member.Id, [Role.Member, Role.Approver]);
        var second = await ReplaceRoles(member.Id, [Role.Approver, Role.Member]);

        Assert.Equal(first.Roles, second.Roles);
    }

    [Fact]
    public async Task ReplaceRoles_RemovesWhatIsNotInTheNewSet()
    {
        Admin();
        var user = User(Role.Approver, Role.Member);

        await ReplaceRoles(user.Id, [Role.Member]);

        Assert.Equal([Role.Member], user.Roles);
    }

    [Fact]
    public async Task ReplaceRoles_AnUnchangedSetWritesNothingAndDoesNotTouch()
    {
        Admin();
        var member = Member();
        var before = member.UpdatedAtUtc;

        await ReplaceRoles(member.Id, [Role.Member]);

        Assert.Equal(before, member.UpdatedAtUtc);
    }

    [Fact]
    public async Task ReplaceRoles_AnUnknownUserIsNotFound()
    {
        Admin();

        await Assert.ThrowsAsync<UserNotFoundException>(() =>
            ReplaceRoles(Guid.NewGuid(), [Role.Member]));
    }

    // **The invariant, through the second door.** Without this check an admin
    // could keep their account active and simply drop the role.
    [Fact]
    public async Task ReplaceRoles_TakingTheRoleFromTheLastActiveAdminIsRefused()
    {
        var admin = Admin();

        var exception = await Assert.ThrowsAsync<LastTenantAdminException>(() =>
            ReplaceRoles(admin.Id, [Role.Member]));

        Assert.Equal(ReasonCodes.LastTenantAdmin, exception.ReasonCode);
        Assert.Contains(Role.TenantAdmin, admin.Roles);
    }

    [Fact]
    public async Task ReplaceRoles_TakingTheRoleIsAllowedWhenAnotherActiveAdminRemains()
    {
        var first = Admin();
        Admin();

        await ReplaceRoles(first.Id, [Role.Member]);

        Assert.DoesNotContain(Role.TenantAdmin, first.Roles);
    }

    // Keeping the role while changing the rest of the set is not a removal, so
    // the guard must not fire — and must not even be consulted.
    [Fact]
    public async Task ReplaceRoles_KeepingTheRoleIsNeverRefused()
    {
        var admin = Admin();

        await ReplaceRoles(admin.Id, [Role.TenantAdmin, Role.Approver]);

        Assert.Contains(Role.TenantAdmin, admin.Roles);
        Assert.Equal(0, _users.LastAdminCountQueries);
    }

    // A deactivated admin is not in the active set, so editing their roles
    // cannot empty it — an admin tidying somebody up before bringing them back.
    [Fact]
    public async Task ReplaceRoles_ADeactivatedAdminsRolesCanBeEditedFreely()
    {
        Admin();
        var departed = Admin();
        departed.Deactivate(ActorId, NowUtc.AddDays(-1));

        await ReplaceRoles(departed.Id, [Role.Member]);

        Assert.Equal([Role.Member], departed.Roles);
    }

    // Granting a role never shrinks the admin set.
    [Fact]
    public async Task ReplaceRoles_GrantingTheAdminRoleIsNeverRefused()
    {
        var admin = Admin();
        var member = Member();

        await ReplaceRoles(member.Id, [Role.TenantAdmin]);
        // ...and now the original can give it up.
        await ReplaceRoles(admin.Id, [Role.Member]);

        Assert.Contains(Role.TenantAdmin, member.Roles);
        Assert.DoesNotContain(Role.TenantAdmin, admin.Roles);
    }

    [Fact]
    public async Task ReplaceRoles_RunsInsideAUnitOfWork()
    {
        Admin();
        var member = Member();

        await ReplaceRoles(member.Id, [Role.Approver]);

        Assert.Equal(1, _unitOfWork.Executions);
    }

    // ---- Helpers ----

    private Task<DeactivateUserCommandResponse> Deactivate(Guid userId, Guid? actorUserId = null) =>
        new DeactivateUserCommandRequestHandler(
                _users, _unitOfWork, new FixedCurrentUser(actorUserId ?? ActorId), _clock)
            .Handle(new DeactivateUserCommandRequest(userId), CancellationToken.None);

    private Task<ReactivateUserCommandResponse> Reactivate(Guid userId) =>
        new ReactivateUserCommandRequestHandler(_users, new FixedCurrentUser(ActorId), _clock)
            .Handle(new ReactivateUserCommandRequest(userId), CancellationToken.None);

    private Task<ReplaceUserRolesCommandResponse> ReplaceRoles(Guid userId, IReadOnlyList<Role> roles) =>
        new ReplaceUserRolesCommandRequestHandler(
                _users, _unitOfWork, new FixedCurrentUser(ActorId), _clock)
            .Handle(new ReplaceUserRolesCommandRequest(userId, roles), CancellationToken.None);

    private User Admin() => User(Role.TenantAdmin);

    private User Member() => User(Role.Member);

    private User User(params Role[] roles)
    {
        var user = new User(
            Guid.NewGuid(),
            OrgId,
            $"user-{Guid.NewGuid():N}@acme.test",
            "hash",
            "A Person",
            ActorId,
            NowUtc.AddDays(-7));

        foreach (var role in roles)
        {
            user.AddRole(role, ActorId, NowUtc.AddDays(-7));
        }

        _users.Users.Add(user);
        return user;
    }

    // Runs the delegate straight through. There is no transaction to simulate —
    // what matters at this level is only *that* the handler asked for one, which
    // is what Executions records. Whether the lock it takes actually serializes
    // two callers is proven against SQL Server, in UserWriteEndpointTests.
    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int Executions { get; private set; }

        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken)
        {
            Executions++;
            return work(cancellationToken);
        }
    }
}
