using FluentValidation;

namespace BookSpace.Application.Features.Resources.ReplaceApprovers;

// Shape only. Whether each id is a real, eligible user is a rule check in the
// handler carrying ReasonCodes.ApproverNotEligible — a validator cannot query
// Users, and must not, or the failure would arrive as a 400 naming a field
// rather than as the deliberately vague 422 that keeps a cross-tenant id secret.
public sealed class ReplaceApproversCommandRequestValidator
    : AbstractValidator<ReplaceApproversCommandRequest>
{
    // Same bound, same reasoning as the availability windows: not a guess at a
    // real limit, just a ceiling on an otherwise unbounded write. Far more
    // generous than any plausible approver list — a resource routing approvals to
    // fifty people has a process problem, not an API problem.
    public const int MaxApproversPerRequest = 50;

    public ReplaceApproversCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // NotNull, not NotEmpty, exactly as for the availability windows: an
        // empty array is a real request meaning "this resource has no approvers".
        // It is refused only when the resource requires approval, and then by the
        // handler with ApproversRequired — a rule, not a shape problem.
        RuleFor(c => c.ApproverUserIds)
            .NotNull()
            .WithMessage("ApproverUserIds is required; send an empty array to clear the list.");

        RuleFor(c => c.ApproverUserIds)
            .Must(ids => ids.Count <= MaxApproversPerRequest)
            .When(c => c.ApproverUserIds is not null)
            .WithMessage($"A resource cannot have more than {MaxApproversPerRequest} approvers.");

        RuleForEach(c => c.ApproverUserIds)
            .NotEmpty()
            .WithMessage("An approver user id cannot be an empty Guid.");

        // Rejected rather than collapsed. The domain applies set semantics, so a
        // repeated id would store cleanly and the response would silently contain
        // fewer entries than the request — the same "never quietly alter what was
        // submitted" principle behind rejecting an oversized pageSize
        // (docs/decisions/0015). It is also a reliable sign of a client bug.
        RuleFor(c => c.ApproverUserIds)
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .When(c => c.ApproverUserIds is not null)
            .WithMessage("ApproverUserIds must not contain duplicates.");
    }
}
