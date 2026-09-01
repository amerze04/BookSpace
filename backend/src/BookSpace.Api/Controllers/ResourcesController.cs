using BookSpace.Api.Authorization;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.ArchiveResource;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Application.Features.Resources.UpdateResource;
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
// member has to browse resources to book one. The write actions carry
// [Authorize(Policy = TenantAdmin)] of their own instead, so the class-level
// policy stays the weaker of the two and a forgotten attribute can only ever
// narrow a write, never widen one. TenantMember also excludes SysAdmin by
// requiring the orgId claim
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
    [ProducesResponseType<PagedResult<ListResourcesQueryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ListResourcesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ListResourcesQueryRequest(request.Page, request.PageSize, request.Sort, request.IncludeArchived),
            cancellationToken);

        return Ok(result);
    }

    // 404 covers both "no such resource" and "another tenant's resource" — the
    // handler cannot tell them apart, and must not (AC-4).
    [HttpGet("{id:guid}")]
    [ProducesResponseType<GetResourceQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetResourceQueryRequest(id), cancellationToken);
        return Ok(result);
    }

    // OrgId and the acting user are not on the wire: both come from the token
    // (ICurrentTenant / ICurrentUser), so neither can be forged by editing a
    // body. Id is not either — the server assigns it.
    public sealed record CreateResourceRequest(
        string Name,
        string? Description,
        string ResourceType,
        int Capacity,
        string TimeZoneId,
        bool RequiresApproval,
        int? MinDurationMinutes,
        int? MaxDurationMinutes);

    // A full representation, not a patch (docs/decisions/0015): every mutable
    // field is supplied and an omitted nullable one means cleared. Same fields
    // as the create request — the id travels in the route instead of the body,
    // so the two cannot disagree.
    public sealed record UpdateResourceRequest(
        string Name,
        string? Description,
        string ResourceType,
        int Capacity,
        string TimeZoneId,
        bool RequiresApproval,
        int? MinDurationMinutes,
        int? MaxDurationMinutes);

    // TenantAdmin on the action, on top of the class-level TenantMember: both
    // must pass, which is what WP-3's AC "non-admins cannot create or edit
    // resources" asks for. It also keeps a SysAdmin out — the TenantAdmin
    // policy admits the role, but TenantMember's orgId-claim requirement does
    // not, and a SysAdmin has no tenant to create a resource in.
    //
    // 400 InvalidTimeZone and 422 ApproversRequired come from the handler as
    // AppExceptions; 403 comes from the authorization policy with no reason
    // code at all. Which one you get says whether you are hitting RBAC or a rule.
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType<CreateResourceCommandResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        CreateResourceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CreateResourceCommandRequest(
                request.Name,
                request.Description,
                request.ResourceType,
                request.Capacity,
                request.TimeZoneId,
                request.RequiresApproval,
                request.MinDurationMinutes,
                request.MaxDurationMinutes),
            cancellationToken);

        // 201 with a Location header pointing at the read endpoint, so a client
        // never has to guess the URL of what it just created.
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType<UpdateResourceCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        Guid id,
        UpdateResourceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new UpdateResourceCommandRequest(
                id,
                request.Name,
                request.Description,
                request.ResourceType,
                request.Capacity,
                request.TimeZoneId,
                request.RequiresApproval,
                request.MinDurationMinutes,
                request.MaxDurationMinutes),
            cancellationToken);

        return Ok(result);
    }

    // FR-3.5. POST /resources/{id}/archive, deliberately not
    // DELETE /resources/{id}.
    //
    // DELETE is the REST idiom for taking something out of a collection, and
    // soft-delete-behind-DELETE is a common pattern — but CLAUDE.md §4.5 says
    // nothing in this system is deleted, and there is no Unarchive (the PRD does
    // not ask for one). A DELETE that silently means "archive, irreversibly"
    // invites a client to assume the row is gone. Naming the transition says
    // what actually happens, and leaves DELETE unimplemented, which is itself
    // the honest answer for a resource that cannot be deleted.
    //
    // No payload, and 200 with the resource's own representation rather than 204:
    // the resource still exists and stays readable (FR-3.5), so the useful reply
    // is the row with isArchived flipped.
    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType<GetResourceQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ArchiveResourceCommandRequest(id), cancellationToken);
        return Ok(result);
    }
}
