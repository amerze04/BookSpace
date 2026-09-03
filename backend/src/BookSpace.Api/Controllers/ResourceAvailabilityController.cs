using BookSpace.Api.Authorization;
using BookSpace.Application.Features.Resources.GetResourceAvailability;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// WP-3 Phase 5: the availability query. Thin by design (WP-2's rule) — bind,
// dispatch, return.
//
// TenantMember, not TenantAdmin: this is the endpoint the PRD's member flow runs
// on ("selects a resource and date -> sees live availability -> picks a slot").
// TenantMember also excludes SysAdmin by requiring the orgId claim
// (docs/decisions/0012), which is why a SysAdmin token gets 403 here.
//
// Its own controller rather than another action on ResourcesController, matching
// BlackoutPeriodsController: the route is nested under a resource, and a
// controller per sub-resource keeps the route prefix declared once instead of
// repeated on every action.
[ApiController]
[Route("resources/{resourceId:guid}/availability")]
[Authorize(Policy = AuthorizationPolicies.TenantMember)]
public sealed class ResourceAvailabilityController : ControllerBase
{
    private readonly ISender _sender;

    public ResourceAvailabilityController(ISender sender)
    {
        _sender = sender;
    }

    // Wire shape lives with the controller, the query with the handler, per
    // docs/decisions/0015 — even though the fields coincide here.
    //
    // No defaults: both dates are required, and an omitted one binds to
    // default(DateOnly) for the validator to refuse by name. A malformed date
    // ("from=yesterday") never reaches the validator — model binding rejects it
    // as a 400 first, the same way a malformed Guid in the route is rejected by
    // its own constraint.
    public sealed record GetResourceAvailabilityRequest(DateOnly From, DateOnly To);

    // 404 covers both "no such resource" and "another tenant's resource" — the
    // handler cannot tell them apart, and must not (AC-4). An *archived*
    // resource is 200 with an empty list and isArchived set, not 404 and not 422:
    // FR-3.5 keeps it readable, and "nothing is bookable" is the true answer.
    [HttpGet]
    [ProducesResponseType<GetResourceAvailabilityQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        Guid resourceId,
        [FromQuery] GetResourceAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new GetResourceAvailabilityQueryRequest(resourceId, request.From, request.To),
            cancellationToken);

        return Ok(result);
    }
}
