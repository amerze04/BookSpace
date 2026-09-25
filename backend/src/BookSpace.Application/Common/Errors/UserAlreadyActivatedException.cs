namespace BookSpace.Application.Common.Errors;

// ErrorKind.Conflict (hardening pass, 2026-09-25, finding 3). The account this
// id names has already set its own password — the state a resend exists to
// help somebody reach, not something to redo. Conflict, not RuleViolation: the
// request is refused by the *current state of the data* (already activated),
// not by a standing rule, and the id itself is fine, which is what
// distinguishes this from UserNotActiveException below.
public sealed class UserAlreadyActivatedException : AppException
{
    public UserAlreadyActivatedException(Guid userId)
        : base(
            ErrorKind.Conflict,
            ReasonCodes.UserAlreadyActivated,
            $"User '{userId}' has already activated their account.")
    {
    }
}
