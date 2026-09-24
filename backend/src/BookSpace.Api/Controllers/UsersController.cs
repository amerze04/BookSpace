using BookSpace.Api.Authorization;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users.CreateUser;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Admin console phase 1 (docs/admin-plan.md) for the read; user management
// phase 3 (docs/user-management-plan.md) for the create. Thin by design (WP-2's
// rule): map the request onto a command or query, dispatch through the
// mediator, return the result. No filtering, ordering or tenant logic here.
//
// **The two actions do not describe the same set of people, and will not until
// phase 4.** GET returns the decision `0018` eligible-approver set; POST creates
// a Member, who is deliberately not in it. That is not drift — see
// ListUsersQueryRequest for why the read is narrower than its route, and the
// plan's phase 4 for the widening that reconciles them.
//
// **The whole controller is TenantAdmin, unlike ResourcesController**, where the
// class policy is the weaker TenantMember so a forgotten attribute can only ever
// narrow a write. There is no member-facing read here to be weaker for: a list of
// people who can approve things is an administrative view, and FR-3.3's member-
// facing answer to "who approves this room" is already on GET /resources/{id} as
// the assigned approver names.
//
// Both policies are still applied, and the order matters. TenantMember requires
// the orgId claim, which a SysAdmin deliberately does not carry (decision `0012`,
// PRD §2: the platform operator must never see tenant content in routine
// operation) — while AuthorizationPolicies.TenantAdmin admits SysAdmin by role.
// Stacking them is what keeps a SysAdmin token out: without TenantMember this
// endpoint would answer a SysAdmin with an empty page, because the tenant query
// filter would match nothing, which is a confusing way to say 403.
[ApiController]
[Route("users")]
[Authorize(Policy = AuthorizationPolicies.TenantMember)]
[Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
public sealed class UsersController : ControllerBase
{
    private readonly ISender _sender;

    public UsersController(ISender sender)
    {
        _sender = sender;
    }

    // Wire shape, per docs/decisions/0015: the HTTP request lives with the
    // controller, the query with the handler, even where the fields coincide.
    // Defaults are repeated here rather than inherited from the query so an
    // omitted parameter binds to the documented default instead of 0.
    public sealed record ListUsersRequest(
        int Page = PagingDefaults.Page,
        int PageSize = PagingDefaults.PageSize,
        string? Sort = null,
        string? Search = null);

    // Returns the decision `0018` eligible-approver set for the caller's tenant,
    // not every user in it — see ListUsersQueryRequest for why the route is
    // broader than the answer.
    //
    // Paging/sorting failures come back as 400 from ValidationBehavior, with
    // per-field errors — not silently clamped (PagingDefaults.MaxPageSize).
    // There is no 404 arm: an empty tenant is an empty page, not a missing thing.
    [HttpGet]
    [ProducesResponseType<PagedResult<ListUsersQueryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ListUsersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ListUsersQueryRequest(
                request.Page,
                request.PageSize,
                request.Sort,
                request.Search),
            cancellationToken);

        return Ok(result);
    }

    // Wire shape for POST /users, per `0015`. No password and no roles — see
    // CreateUserCommandRequest; the recipient chooses the first and the handler
    // assigns Member as the second.
    public sealed record CreateUserRequest(string Email, string FullName);

    // User management phase 3. Provisions a colleague and emails them an
    // invitation (PRD §2's Tenant Administrator persona; there is no FR for user
    // management and docs/user-management-plan.md §1 says so rather than
    // inventing one).
    //
    // **201 with no Location header**, unlike POST /resources. There is no
    // GET /users/{id} to point at — the directory read is phase 4 and reads the
    // collection — and CreatedAtAction against a route that does not exist
    // throws at runtime rather than omitting the header. A Location will be
    // added when there is somewhere for it to go.
    //
    // 409 is EmailAlreadyInUse, and it says the same thing whether the address
    // belongs to this tenant or another (decision `0010`, §3.2).
    //
    // **The 201 body carries a live activation link.** That is deliberate
    // (§4.3) and it makes this response something to show once and not store —
    // see CreateUserCommandResponse.
    [HttpPost]
    [ProducesResponseType<CreateUserCommandResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CreateUserCommandRequest(request.Email, request.FullName),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result);
    }
}
