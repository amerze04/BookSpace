namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (user management phase 5). The request would leave
// the tenant with no active TenantAdmin.
//
// RuleViolation, not Conflict: the request is well-formed and unambiguous, and
// what refuses it is a rule about what this system may be left in — not a
// collision with something another actor did. It is also not a transient
// failure that a retry might get past; the admin has to change something first.
//
// **The message is the unusual part, and it is deliberate.** Every other
// exception here keeps its message for the log, because nothing at this layer
// knows what is safe to tell a client. This one says what to do instead, and it
// still goes only to the log — the *client-facing* wording belongs to the
// screen (phase 7), whose job is to render this as a refusal an admin can act
// on rather than as a wall. Recorded here so the two do not drift apart: give
// somebody else the administrator role first.
//
// No user id and no count. Which admin, and how many there are, is not
// information the refusal needs to carry — and the caller knows who they were
// acting on.
public sealed class LastTenantAdminException : AppException
{
    public LastTenantAdminException()
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.LastTenantAdmin,
            "A tenant must keep at least one active administrator. "
            + "Give somebody else the administrator role before removing this one.")
    {
    }
}
