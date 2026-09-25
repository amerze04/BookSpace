namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (hardening pass, 2026-09-25, finding 3). The account
// this id names is deactivated. A fresh activation link would still refuse to
// redeem — ActivateAccountCommandRequestHandler already checks IsActive
// (FR-2.4) — so resending one would be a link that looks live and is not.
// RuleViolation, not Conflict: nothing collided, and the fix is for the admin
// to act first (reactivate), not to retry the same request.
public sealed class UserNotActiveException : AppException
{
    public UserNotActiveException(Guid userId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.UserNotActive,
            $"User '{userId}' is deactivated; reactivate before resending an invitation.")
    {
    }
}
