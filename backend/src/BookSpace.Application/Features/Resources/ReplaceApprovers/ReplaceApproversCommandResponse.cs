namespace BookSpace.Application.Features.Resources.ReplaceApprovers;

// The 200 body of PUT /resources/{id}/approvers.
//
// Returns the list rather than 204 for the reason the schedule endpoint does:
// the server resolved every id to a person and normalized the order, and a
// client that got 204 would have to re-read to see either. It is also the
// cheapest way for an admin to confirm they assigned the people they meant —
// a wrong-but-eligible Guid is otherwise invisible.
//
// RequiresApproval is echoed because the two are coupled: emptying this list on a
// resource that requires approval is refused (ApproversRequired), so a client
// needs the flag in view to understand why.
public sealed record ReplaceApproversCommandResponse(
    Guid ResourceId,
    bool RequiresApproval,
    IReadOnlyList<AssignedApprover> Approvers);
