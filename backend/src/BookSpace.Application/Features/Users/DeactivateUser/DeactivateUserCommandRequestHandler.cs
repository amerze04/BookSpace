using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.DeactivateUser;

// FR-2.4. Writes the flag the auth stack has honoured since WP-2 and nothing
// outside SeedData could set.
//
// **The whole body runs inside IUnitOfWork.ExecuteAsync, and that is the point
// of this handler rather than an implementation detail.** The last-admin guard
// is a read-check-write across two rows; the read takes UPDLOCK/HOLDLOCK, and a
// lock taken outside a transaction is released immediately, which looks
// identical in every single-request test and is worth nothing under two. See
// docs/user-management-plan.md §4.5.
//
// Everything the decision depends on is read *inside* the delegate, because the
// execution strategy can run it again — and a retry happens precisely when the
// data moved (1205, the deadlock two admins racing here can produce).
public sealed class DeactivateUserCommandRequestHandler
    : IRequestHandler<DeactivateUserCommandRequest, DeactivateUserCommandResponse>
{
    private readonly IUserRepository _users;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public DeactivateUserCommandRequestHandler(
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

    public Task<DeactivateUserCommandResponse> Handle(
        DeactivateUserCommandRequest request,
        CancellationToken cancellationToken)
    {
        // From the token, never the payload. See
        // CreateResourceCommandRequestHandler for why this is an
        // InvalidOperationException rather than an AppException.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (the deactivation audit trail).");

        return _unitOfWork.ExecuteAsync(
            async token =>
            {
                // Tenant-filtered, so another tenant's real id arrives as null
                // and leaves as UserNotFound, indistinguishable from an id that
                // exists nowhere (AC-4).
                var user = await _users.FindForUpdateAsync(request.UserId, token)
                    ?? throw new UserNotFoundException(request.UserId);

                // Idempotent, and the early return is deliberate rather than
                // lazy — the same call ArchiveResource makes. Asking for a state
                // that already holds is the outcome the caller wanted, not a rule
                // violation, and re-writing it would move UpdatedAtUtc so that
                // "last changed" started meaning "last asked about".
                //
                // Before the guard, too: deactivating an already-inactive user
                // cannot empty the admin set, so there is nothing to check and no
                // reason to take a lock.
                if (!user.IsActive)
                {
                    return user.ToDeactivateResponse();
                }

                await UserWriteRules.EnsureNotTheLastAdminBeingDeactivatedAsync(_users, user, token);

                user.Deactivate(actorUserId, _clock.UtcNow);
                await _users.SaveChangesAsync(token);

                return user.ToDeactivateResponse();
            },
            cancellationToken);
    }
}
