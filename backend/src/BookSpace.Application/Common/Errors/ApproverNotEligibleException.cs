namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-3.3). One or more of the user ids in an approver
// assignment cannot be an approver of this resource.
//
// **One code for three causes**, deliberately: the user is in another tenant,
// the user lacks both the Approver and TenantAdmin roles, or the user is
// deactivated. A code naming which one would confirm that a cross-tenant id
// exists somewhere, which is the disclosure AC-4 forbids — the reasoning is in
// docs/decisions/0016, and it is why the code is not the ApproverNotInTenant
// that docs/wp3-plan.md originally proposed.
//
// RuleViolation, not NotFound: within the caller's tenant the user genuinely
// does not exist, but the request is a well-formed assignment refused by a rule,
// and a 404 on a PUT whose own path resource *does* exist reads as "the resource
// is missing".
//
// The offending ids are in the message for the log only. Handing them back would
// be safe (the client just sent them) but pointless without saying *why* each
// failed, and saying why is the thing that leaks.
public sealed class ApproverNotEligibleException : AppException
{
    public ApproverNotEligibleException(IEnumerable<Guid> userIds)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.ApproverNotEligible,
            // Null-tolerant because an exception constructor must never itself
            // throw, and because AppExceptionCatalogueTests builds every subclass
            // reflectively with default arguments to check its kind and code.
            "Not eligible to approve for this tenant: "
            + string.Join(", ", userIds ?? Enumerable.Empty<Guid>()) + ".")
    {
    }
}
