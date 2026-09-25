using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ReplaceUserRoles;

// FR-1.5 and PRD §2's persona line. The second write that can empty the tenant's
// set of active administrators, and therefore the second one that runs its guard
// under a lock — see DeactivateUserCommandRequestHandler for why the transaction
// is load-bearing rather than decorative.
//
// Same ordering discipline as every other write handler here: every rule check
// runs before any mutator, so a rejected request leaves the tracked aggregate
// untouched.
public sealed class ReplaceUserRolesCommandRequestHandler
    : IRequestHandler<ReplaceUserRolesCommandRequest, ReplaceUserRolesCommandResponse>
{
    private readonly IUserRepository _users;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ReplaceUserRolesCommandRequestHandler(
        IUserRepository users,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _users = users;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    public Task<ReplaceUserRolesCommandResponse> Handle(
        ReplaceUserRolesCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (the role-change audit trail).");

        return _unitOfWork.ExecuteAsync(
            async token =>
            {
                var user = await _users.FindForUpdateAsync(request.UserId, token)
                    ?? throw new UserNotFoundException(request.UserId);

                // Nothing here refuses a *deactivated* user's roles being
                // edited. Deactivation is reversible, so an admin sorting out
                // somebody's roles before bringing them back is ordinary — and
                // the guard below already knows an inactive user is not in the
                // active-admin set, so the edit cannot empty it.
                await UserWriteRules.EnsureNotTheLastAdminLosingTheRoleAsync(
                    _users, user, request.Roles, token);

                // A no-op replace tracks no change and therefore writes nothing
                // — User.ReplaceRoles only Touches when the set actually moved.
                // SaveChangesAsync is still called rather than skipped, because
                // "did anything change?" is the entity's question to answer, not
                // this handler's to guess at.
                user.ReplaceRoles(request.Roles, actorUserId, _clock.UtcNow);
                await _users.SaveChangesAsync(token);

                return user.ToRolesResponse();
            },
            cancellationToken);
    }
}
