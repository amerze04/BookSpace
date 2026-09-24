namespace BookSpace.Application.Common.Errors;

// ErrorKind.Conflict. POST /users was given an email address that already
// belongs to an account.
//
// Conflict rather than Validation: the address is perfectly well-formed, and
// what refuses it is the current state of the data — which is also why it may
// succeed later, after the other account is renamed or the admin corrects a
// typo.
//
// **It carries no email address and no tenant**, and that is the whole point.
// Decision `0010` made email unique across the platform, so a collision may be
// with an account in an organization this admin cannot see; a message or a
// payload that distinguished the two cases would confirm that an address exists
// somewhere else, which AC-4 forbids. See ReasonCodes.EmailAlreadyInUse and
// docs/user-management-plan.md §3.2.
//
// Not even in the log message, unlike ApproverNotEligibleException next door,
// which does log the offending ids: those came from the caller's own request
// body and named their own tenant's users. Here the interesting fact — *whose*
// account already holds the address — is precisely the one nothing on this path
// is allowed to learn, and the address the caller sent is already in the
// request log.
public sealed class EmailAlreadyInUseException : AppException
{
    public EmailAlreadyInUseException()
        : base(
            ErrorKind.Conflict,
            ReasonCodes.EmailAlreadyInUse,
            "The email address is already in use by an existing account.")
    {
    }
}
