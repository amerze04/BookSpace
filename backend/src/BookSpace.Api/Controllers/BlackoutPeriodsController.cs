using BookSpace.Api.Authorization;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.BlackoutPeriods;
using BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.DeleteBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// FR-3.4. Thin by design (WP-2's rule): map the request onto a command, dispatch
// through the mediator, return the result.
//
// Its own controller rather than more actions on ResourcesController, because a
// BlackoutPeriod is its own aggregate — Resource has no navigation to it, and
// unlike availability windows it is not created through the resource. The route
// is still nested, since a blackout has no meaning apart from the resource it
// blocks and the AC-4 404 has to come from the same place.
//
// Class-level TenantMember with TenantAdmin on the write, exactly as
// ResourcesController does it: the weaker policy sits on the class so a
// forgotten attribute can only ever narrow a write, never widen one. TenantMember
// also excludes SysAdmin by requiring the orgId claim (docs/decisions/0012),
// which is why a SysAdmin token gets 403 here.
[ApiController]
[Route("resources/{resourceId:guid}/blackout-periods")]
[Authorize(Policy = AuthorizationPolicies.TenantMember)]
public sealed class BlackoutPeriodsController : ControllerBase
{
    private readonly ISender _sender;

    public BlackoutPeriodsController(ISender sender)
    {
        _sender = sender;
    }

    // Wire shape, per docs/decisions/0015: the HTTP request lives with the
    // controller, the query with the handler, even where the fields coincide.
    // Defaults are repeated here rather than inherited from the query so an
    // omitted parameter binds to the documented default instead of 0/null.
    public sealed record ListBlackoutPeriodsRequest(
        DateTime? From = null,
        DateTime? To = null,
        int Page = PagingDefaults.Page,
        int PageSize = PagingDefaults.PageSize,
        string? Sort = null);

    // On TenantMember, not TenantAdmin: a member picking a time needs to know
    // when the room is blacked out, the same reason the read detail carries the
    // availability windows. 404 covers both "no such resource" and "another
    // tenant's resource" — the handler cannot tell them apart, and must not
    // (AC-4).
    [HttpGet]
    [ProducesResponseType<PagedResult<ListBlackoutPeriodsQueryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        Guid resourceId,
        [FromQuery] ListBlackoutPeriodsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ListBlackoutPeriodsQueryRequest(
                resourceId, request.From, request.To, request.Page, request.PageSize, request.Sort),
            cancellationToken);

        return Ok(result);
    }

    // ResourceId is not on the wire — it travels in the route, so the two cannot
    // disagree. OrgId and the acting user are not either: both come from the
    // token, so neither can be forged by editing a body.
    public sealed record CreateBlackoutPeriodRequest(
        DateTime StartsAtUtc,
        DateTime EndsAtUtc,
        string? Reason);

    // 201, and the body is worth more than the usual echo: decision 0001 gives a
    // blackout absolute priority over existing bookings, so creating one cancels
    // every booking it overlaps and notifies each owner. The response lists them
    // (CancelledBookings), because an admin who cancelled eleven people's
    // meetings should be told so in the reply rather than discover it later.
    //
    // 422 BlackoutPeriodElapsed is the one status here the sibling endpoints do
    // not produce: a blackout whose interval is entirely in the past blocks
    // nothing. 422 ResourceArchived comes from the same rule that refuses an
    // edit. 403 comes from the authorization policy with no reason code at all —
    // which one you get says whether you are hitting RBAC or a rule.
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType<CreateBlackoutPeriodCommandResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        Guid resourceId,
        CreateBlackoutPeriodRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CreateBlackoutPeriodCommandRequest(
                resourceId, request.StartsAtUtc, request.EndsAtUtc, request.Reason),
            cancellationToken);

        // Location points at the list rather than at a per-blackout GET, which
        // does not exist: nothing needs to fetch one blackout by id, and adding a
        // read endpoint purely to have somewhere to point would be inventing API
        // surface to satisfy a header. The list is where the new row is visible.
        return CreatedAtAction(nameof(List), new { resourceId }, result);
    }

    // A full representation, not a patch (docs/decisions/0015): every mutable
    // field is supplied and an omitted Reason means cleared. Same fields as the
    // create request — both ids travel in the route instead of the body, so they
    // cannot disagree with it.
    public sealed record UpdateBlackoutPeriodRequest(
        DateTime StartsAtUtc,
        DateTime EndsAtUtc,
        string? Reason);

    // Re-runs decision 0001's cascade over the new interval, so this response
    // carries cancelledBookings exactly as the create does — moving a blackout
    // blacks out bookings the original never touched.
    //
    // Two different 404s here, which is deliberate: ResourceNotFound means the
    // resource in the path is wrong (or another tenant's), BlackoutPeriodNotFound
    // means the resource is fine and the blackout is not. An admin debugging a
    // script needs to know which half of the URL to fix.
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType<UpdateBlackoutPeriodCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        Guid resourceId,
        Guid id,
        UpdateBlackoutPeriodRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new UpdateBlackoutPeriodCommandRequest(
                resourceId, id, request.StartsAtUtc, request.EndsAtUtc, request.Reason),
            cancellationToken);

        return Ok(result);
    }

    // DELETE, and a real one — unlike POST /resources/{id}/archive, which is
    // deliberately not a DELETE. The difference is what §4.5 protects: a resource
    // has booking history that must stay readable, so it is archived; a blackout
    // has none, because the reason a booking was cancelled is a text snapshot on
    // the booking rather than a reference to this row. So DELETE here means what
    // a client expects it to mean, and using it is honest.
    //
    // What it does *not* mean is "undo": bookings the blackout cancelled stay
    // cancelled. 204 with no body, since there is nothing useful to say about a
    // row that no longer exists — and deliberately 404, not 204, on a second
    // call, because this endpoint cannot tell "already deleted" from "another
    // tenant's id" and must not accept the latter (AC-4).
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(
        Guid resourceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteBlackoutPeriodCommandRequest(resourceId, id), cancellationToken);

        return NoContent();
    }
}
