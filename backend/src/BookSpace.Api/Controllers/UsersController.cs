using BookSpace.Api.Authorization;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users;
using BookSpace.Application.Features.Users.CreateUser;
using BookSpace.Application.Features.Users.DeactivateUser;
using BookSpace.Application.Features.Users.GetUserById;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Application.Features.Users.ReactivateUser;
using BookSpace.Application.Features.Users.ReplaceUserRoles;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Admin console phase 1 (docs/admin-plan.md) for the read; user management
// phases 3 and 4 (docs/user-management-plan.md) for the create and the
// directory. Thin by design (WP-2's rule): map the request onto a command or
// query, dispatch through the mediator, return the result. No filtering,
// ordering or tenant logic here.
//
// **GET answers two different sets of people, and `scope` is which.** Omitted
// it is the decision `0018` eligible-approver set — so a Member created by POST
// here does not appear in it, which was a real gap between phases 3 and 4 and is
// now a parameter away. `scope=All` is the directory. See UserScope for why the
// narrow set is the default rather than the wide one.
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
        string? Search = null,
        UserScope Scope = UserScope.EligibleApprovers);

    // Two answers, one route, chosen by `scope`. Omitted gives the decision
    // `0018` eligible-approver set for the caller's tenant — the approvers
    // picker's read, and what this endpoint answered before the directory
    // existed. `scope=All` gives every user in the tenant, deactivated accounts
    // included. See UserScope for why the narrow one is the default.
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
                request.Search,
                request.Scope),
            cancellationToken);

        return Ok(result);
    }

    // User management phase 7. None of the writes below (deactivate, reactivate,
    // replace roles) returns a single user in a shape meant for a whole screen,
    // and the directory (List, above) has no id filter — so the user detail
    // screen had nothing to load from on a direct link, a bookmark, or a reload.
    //
    // 404 covers both "no such user" and "another tenant's real id" — the
    // handler cannot tell them apart, and must not (AC-4). A deactivated user is
    // still readable by id: only the directory's default *view* could hide one,
    // and scope=All already does not.
    [HttpGet("{id:guid}")]
    [ProducesResponseType<GetUserByIdQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetUserByIdQueryRequest(id), cancellationToken);
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

    // User management phase 5. FR-2.4: the write the auth stack has been waiting
    // for since WP-2 — login already refuses an inactive user, and refresh
    // refuses *and* revokes the whole token family.
    //
    // POST /{id}/deactivate rather than PATCH, matching POST
    // /resources/{id}/archive: a state transition with no payload. Idempotent —
    // deactivating an already-inactive user returns the current state and writes
    // nothing.
    //
    // 422 is LastTenantAdmin: a tenant must keep at least one active
    // administrator, and the check runs under a lock inside the write's own
    // transaction (docs/user-management-plan.md §4.5).
    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType<DeactivateUserCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new DeactivateUserCommandRequest(id), cancellationToken);
        return Ok(result);
    }

    // The reverse, and it exists where an "unarchive" pointedly does not:
    // FR-3.5 makes archiving a resource irreversible on purpose, while
    // deactivating a person is a reversible state.
    //
    // No 422 arm — reactivating can only grow the set of active administrators,
    // so the last-admin guard has nothing to say about it.
    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType<ReactivateUserCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ReactivateUserCommandRequest(id), cancellationToken);
        return Ok(result);
    }

    // Wire shape for PUT /users/{id}/roles. The id is in the path, so the body
    // carries only the new set.
    public sealed record ReplaceUserRolesRequest(IReadOnlyList<Role> Roles);

    // FR-1.5. **Replace-the-set**, matching PUT /resources/{id}/approvers — see
    // ReplaceUserRolesCommandRequest for why, and note that phase 7's screen
    // follows this shape rather than the other way round (admin-plan.md §4.1).
    //
    // 400 covers an empty set, a duplicate, and SysAdmin — that last one is a
    // privilege-escalation guard rather than a formatting rule; see the
    // validator. 422 is LastTenantAdmin, checked under the same lock as
    // deactivation.
    [HttpPut("{id:guid}/roles")]
    [ProducesResponseType<ReplaceUserRolesCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceRoles(
        Guid id,
        ReplaceUserRolesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ReplaceUserRolesCommandRequest(id, request.Roles ?? []),
            cancellationToken);

        return Ok(result);
    }
}
