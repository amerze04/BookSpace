namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation. FR-3.3 reads "marked RequiresApproval, with one or
// more assigned approvers", so RequiresApproval with an empty approver list is
// not a valid resting state.
//
// No resource id: this is thrown on create as well as edit, and on create there
// is no id yet. Nothing is lost — the correlation id already ties the response
// to the request that caused it.
public sealed class ApproversRequiredException : AppException
{
    public ApproversRequiredException()
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.ApproversRequired,
            "A resource cannot require approval with no approvers assigned.")
    {
    }
}
