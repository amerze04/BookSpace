using BookSpace.Api.Authorization;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// FR-4.x. Thin by design (WP-2's rule): map the request onto a command, dispatch
// through the mediator, return the result.
//
// **A top-level route, not nested under /resources/{id}.** A booking is its own
// aggregate: it belongs to a member as much as to a resource, and Phase 2's list
// spans every resource the member has booked. Blackout periods are nested
// because they have no meaning apart from the resource they block; a booking
// does.
//
// TenantMember, which is the point of the policy — a member creating their own
// bookings is the platform's main flow (PRD §2). It also excludes SysAdmin by
// requiring the orgId claim (docs/decisions/0012), so a Platform Operator token
// gets 403 here rather than the ability to book into a tenant.
[ApiController]
[Route("bookings")]
[Authorize(Policy = AuthorizationPolicies.TenantMember)]
public sealed class BookingsController : ControllerBase
{
    private readonly ISender _sender;

    public BookingsController(ISender sender)
    {
        _sender = sender;
    }

    // Wire shape, per docs/decisions/0015: the HTTP request lives with the
    // controller, the command with the handler, even where the fields coincide.
    //
    // Quantity defaults here rather than in the command, so an omitted value
    // binds to 1 — the smallest legal booking and the only one an exclusive
    // resource accepts — instead of to 0, which the validator would then have to
    // reject as a field error for a field the client never sent.
    //
    // UserId is absent on purpose: a member books for themselves, and the actor
    // comes from the token so it cannot be forged by editing a body.
    public sealed record CreateBookingRequest(
        Guid ResourceId,
        DateTime StartsAtUtc,
        DateTime EndsAtUtc,
        int Quantity = 1,
        string? Title = null);

    // 201, with the created booking — Confirmed, or Pending when the resource
    // requires approval (FR-7.1), which is why the response is more than an echo.
    //
    // The statuses are the machine-readable rejection contract FR-4.5 asks for,
    // and they come from the reason code's ErrorKind rather than from anything
    // written here (decision 0016):
    //
    //   404 ResourceNotFound — also another tenant's real id (AC-4)
    //   422 ResourceArchived, OutsideAvailability, BlackoutPeriod,
    //       BookingDurationOutOfRange, BookingInThePast — refused by a rule
    //   409 SlotUnavailable, CapacityExceeded — refused by what already exists,
    //       the second decided under dbo.CreateBooking's lock (AC-1)
    //   400 ValidationFailed — malformed request, with per-field errors
    //   403 — the authorization policy, with no reason code at all; which one you
    //       get says whether you are hitting RBAC or a rule
    [HttpPost]
    [ProducesResponseType<CreateBookingCommandResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CreateBookingCommandRequest(
                request.ResourceId,
                request.StartsAtUtc,
                request.EndsAtUtc,
                request.Quantity,
                request.Title),
            cancellationToken);

        // Location is left to Phase 2's GET /bookings/{id}, which does not exist
        // yet. Created(string?, object?) with a null location emits the 201 and
        // the body without a Location header rather than pointing at an action
        // that would 404 — the alternative, inventing a read endpoint now purely
        // to satisfy a header, is API surface built for a header's sake.
        return Created((string?)null, result);
    }
}
