using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ReactivateUser;

// FR-2.4's other half.
//
// **No IUnitOfWork here, unlike its two siblings, and no lock.** The last-admin
// guard exists to stop the set of active TenantAdmins being emptied;
// reactivating can only ever add to it. There is nothing to serialize against,
// so wrapping this in a transaction would be ceremony that implies a rule
// nobody is enforcing — which is worse than not having one, because the next
// reader would trust it.
public sealed class ReactivateUserCommandRequestHandler
    : IRequestHandler<ReactivateUserCommandRequest, ReactivateUserCommandResponse>
{
    private readonly IUserRepository _users;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ReactivateUserCommandRequestHandler(
        IUserRepository users,
        ICurrentUser currentUser,
        IClock clock)
    {
        _users = users;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<ReactivateUserCommandResponse> Handle(
        ReactivateUserCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (the reactivation audit trail).");

        var user = await _users.FindForUpdateAsync(request.UserId, cancellationToken)
            ?? throw new UserNotFoundException(request.UserId);

        // Idempotent, for the same reasons as its sibling: the caller asked for
        // a state that already holds, and a no-op must not move UpdatedAtUtc.
        if (user.IsActive)
        {
            return user.ToReactivateResponse();
        }

        user.Reactivate(actorUserId, _clock.UtcNow);
        await _users.SaveChangesAsync(cancellationToken);

        return user.ToReactivateResponse();
    }
}
