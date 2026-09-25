using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ReissueInvitation;

// POST /users/{id}/invitation. TenantAdmin only (see UsersController), and
// stacked with AuthorizationPolicies.ActiveTenantAdminWrite — see finding 1 of
// the 2026-09-25 hardening pass — since this is exactly the kind of
// user-management write a demoted or deactivated admin's stale token must not
// still be able to perform.
//
// Hardening pass, 2026-09-25 (finding 3): the recovery path this application
// did not have. Before this, an activation link that expired or never arrived
// left an account with no route in at all — POST /users could not help,
// because the account already exists and email is unique platform-wide
// (decision `0010`). This issues a fresh token, supersedes anything still
// live for the same user (so the account never has two simultaneously usable
// credentials outstanding), and sends a new invitation.
public sealed record ReissueInvitationCommandRequest(Guid UserId)
    : IRequest<ReissueInvitationCommandResponse>;
