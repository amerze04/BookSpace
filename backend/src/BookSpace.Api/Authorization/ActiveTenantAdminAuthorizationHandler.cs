using BookSpace.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace BookSpace.Api.Authorization;

// Hardening pass, 2026-09-25 (finding 1). AuthorizationPolicies.TenantAdmin —
// RequireRole against the JWT's role claim — is only as fresh as the access
// token, up to 15 minutes (decision `0009`). That is fine for FR-2.4's own
// promise ("loses access... on next token refresh"), because the danger there
// is a *stale grant slowly expiring*. It is not fine for the four
// user-management writes on UsersController: there the danger is the reverse
// — a token minted five minutes ago can still *add* privilege back, and the
// last-admin guard has nothing to say about a call that grows the admin set
// rather than emptying it.
//
// Concretely: Alice deactivates Bob (a TenantAdmin). Bob's access token still
// carries the TenantAdmin role claim for up to 15 more minutes. Bob calls
// `POST /users/{bobId}/reactivate` with that stale token and
// AuthorizationPolicies.TenantAdmin alone lets it through, because nothing
// re-reads Bob's actual row. Same shape for Alice removing Bob's TenantAdmin
// role and Bob restoring it himself with `PUT /users/{bobId}/roles`.
//
// So this re-reads the actor's own row, inside the same tenant-filtered query
// every other read in this request would use (IUserRepository.
// IsCurrentlyActiveTenantAdminAsync), and only succeeds when all three hold:
// the row still exists in this tenant (AC-4's own fail-closed shape — the
// query filter matches nothing otherwise), it is IsActive, and TenantAdmin is
// currently among its roles. Stacked alongside
// AuthorizationPolicies.TenantAdmin on the same actions (see
// AddBookSpacePolicies and UsersController), not in place of it — a token with
// no role claim at all is refused before this ever queries anything, the same
// "cheap check first" shape TenantMember + TenantAdmin already use together.
//
// Deliberately scoped to only the four user-management writes rather than the
// whole TenantAdmin surface (ResourcesController and friends): CLAUDE.md's own
// rule is not to make every endpoint pay a database round trip it does not
// need, and this class of bug is specific to routes that can revoke or grant
// TenantAdmin itself. The same risk plausibly exists elsewhere in the app —
// flagged, not fixed here (out of this pass's scope).
internal sealed class ActiveTenantAdminAuthorizationHandler : AuthorizationHandler<ActiveTenantAdminRequirement>
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserRepository _users;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ActiveTenantAdminAuthorizationHandler(
        ICurrentUser currentUser,
        IUserRepository users,
        IHttpContextAccessor httpContextAccessor)
    {
        _currentUser = currentUser;
        _users = users;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveTenantAdminRequirement requirement)
    {
        // No `sub` claim at all: not Succeed()'d, so the policy fails closed.
        // Unreachable in practice behind TenantMember + TenantAdmin, but this
        // handler must not assume the other policies always run first.
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var cancellationToken = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        if (await _users.IsCurrentlyActiveTenantAdminAsync(userId.Value, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}
