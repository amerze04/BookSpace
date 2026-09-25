namespace BookSpace.Application.Common.Errors;

// ErrorKind.NotFound (user management phase 5). No user with this id is visible
// to the caller.
//
// **Two causes, one answer**, following ResourceNotFoundException and
// BookingNotFoundException: the id exists nowhere, or it belongs to another
// tenant. IUserRepository reads through the tenant-filtered DbSet, so a
// cross-tenant id simply does not come back — the handler could not tell them
// apart even if it wanted to, which is what makes this safe (AC-4).
//
// The id is in the message for the log only. Handing it back would be harmless,
// since the caller just sent it, and pointless.
public sealed class UserNotFoundException : AppException
{
    public UserNotFoundException(Guid userId)
        : base(
            ErrorKind.NotFound,
            ReasonCodes.UserNotFound,
            $"No user '{userId}' is visible to this tenant.")
    {
    }
}
