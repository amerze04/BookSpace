using BookSpace.Api.Authorization;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// FR-3.1 / FR-3.5. Thin by design (WP-2's rule): map the request onto a query,
// dispatch through the mediator, return the result. No filtering, ordering or
// tenant logic here.
//
// TenantMember on the reads, deliberately not TenantAdmin. WP-3's AC says
// "non-admins cannot create or edit resources" — create and edit, not read: a
// member has to browse resources to book one. The write endpoints in step 3 get
// [Authorize(Policy = TenantAdmin)] on the action instead, so the class-level
// policy stays the weaker of the two and a forgotten attribute cannot widen a
// write. TenantMember also excludes SysAdmin by requiring the orgId claim
// (docs/decisions/0012), which is why a SysAdmin token gets 403 here.
[ApiController]
[Route("resources")]
[Authorize(Policy = AuthorizationPolicies.TenantMember)]
public sealed class ResourcesController : ControllerBase
{
    private readonly ISender _sender;

    public ResourcesController(ISender sender)
    {
        _sender = sender;
    }

    // Wire shape, per docs/decisions/0015: the HTTP request lives with the
    // controller, the query with the handler, even where the fields coincide.
    // Defaults are repeated here rather than inherited from the query so an
    // omitted parameter binds to the documented default instead of 0/false.
    public sealed record ListResourcesRequest(
        int Page = PagingDefaults.Page,
        int PageSize = PagingDefaults.PageSize,
        string? Sort = null,
        bool IncludeArchived = false);

    // Paging/sorting failures come back as 400 from ValidationBehavior, with
    // per-field errors — not silently clamped (PagingDefaults.MaxPageSize).
    [HttpGet]
    [ProducesResponseType<PagedResult<ResourceSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ListResourcesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ListResourcesQuery(request.Page, request.PageSize, request.Sort, request.IncludeArchived),
            cancellationToken);

        return Ok(result);
    }

    // 404 covers both "no such resource" and "another tenant's resource" — the
    // handler cannot tell them apart, and must not (AC-4).
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ResourceDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetResourceQuery(id), cancellationToken);
        return Ok(result);
    }
}
