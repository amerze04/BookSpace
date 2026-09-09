using BookSpace.Api.Authorization;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.ApproveBooking;
using BookSpace.Application.Features.Bookings.CancelBooking;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Application.Features.Bookings.RejectBooking;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;
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

        // Phase 2a supplied the read endpoint the 201 had nothing to point at,
        // so this is now a proper CreatedAtAction. Named by nameof(GetById)
        // rather than a hand-written path, so a route rename cannot leave the
        // header pointing at nothing.
        //
        // The header is honest for both statuses: a Pending booking exists and
        // is readable at that URL just as a Confirmed one is.
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    // Wire shape, per docs/decisions/0015. Defaults are repeated here rather
    // than inherited from the query so an omitted parameter binds to the
    // documented default instead of 0/null/false.
    //
    // `?status=Confirmed` and `?scope=tenant` bind by name, case-insensitively:
    // ASP.NET Core parses an enum query value with Enum.TryParse, which accepts
    // the name *and* the underlying number, so `status=1` is also accepted. The
    // validator rejects a number that is not a defined member, which
    // Enum.TryParse would otherwise let through as an undefined value matching
    // no row (see ListBookingsQueryRequestValidator).
    //
    // UserId and Scope are TenantAdmin-only (decision 0002); a plain member
    // sending either gets 400 ValidationFailed naming the field, not a quietly
    // narrowed 200.
    public sealed record ListBookingsRequest(
        DateTime? From = null,
        DateTime? To = null,
        BookingStatus? Status = null,
        Guid? ResourceId = null,
        Guid? UserId = null,
        BookingScope Scope = BookingScope.Own,
        int Page = PagingDefaults.Page,
        int PageSize = PagingDefaults.PageSize,
        string? Sort = null);

    // FR-4.4, the "view" half. The caller's own bookings by default, whoever
    // they are; a TenantAdmin can widen with `userId` or `scope`.
    //
    // Paging/sorting/filter failures come back as 400 from ValidationBehavior
    // with per-field errors — not silently clamped (PagingDefaults.MaxPageSize).
    [HttpGet]
    [ProducesResponseType<PagedResult<ListBookingsQueryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ListBookingsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ListBookingsQueryRequest(
                request.From,
                request.To,
                request.Status,
                request.ResourceId,
                request.UserId,
                request.Scope,
                request.Page,
                request.PageSize,
                request.Sort),
            cancellationToken);

        return Ok(result);
    }

    // FR-4.4. One booking, if this caller may see it.
    //
    // **404 for a booking the caller may not see, never 403** — another member's
    // booking, another tenant's booking and an id that exists nowhere are
    // byte-identical. A 403 would confirm the booking exists and leak who is
    // holding which resource, which is the disclosure AC-4 rules out across
    // tenants, applied within one (see BookingNotFoundException).
    [HttpGet("{id:guid}")]
    [ProducesResponseType<GetBookingQueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetBookingQueryRequest(id), cancellationToken);
        return Ok(result);
    }

    // The body is optional in full: a cancellation with no reason is legal, so
    // POSTing nothing at all has to work. Hence [FromBody] with a default rather
    // than a required parameter, which would make an empty body a 400.
    //
    // No actor field, deliberately: who cancelled comes from the token, so an
    // admin cannot attribute their own cancellation to the booking's owner by
    // editing a body (decision 0002 records the actor distinctly on purpose).
    public sealed record CancelBookingRequest(string? Reason = null);

    // FR-4.4 and decision 0002, the "cancel" half. A member cancels their own; a
    // TenantAdmin cancels any booking in their tenant.
    //
    // **POST .../cancel, not DELETE** — §4.5 deletes nothing, and a cancellation
    // records an actor, a time and a reason, so a DELETE would misdescribe
    // itself. Same argument that made archive a POST in WP-3.
    //
    // The statuses:
    //
    //   200 — cancelled, with the freed interval and who cancelled it
    //   404 BookingNotFound — no such booking *for this caller*: another
    //       member's, another tenant's, or nonexistent, all byte-identical
    //   422 BookingNotCancellable — already terminal, or already ended
    //   409 ConcurrencyConflict — two simultaneous cancels; RowVersion picks one
    //   400 ValidationFailed — an over-long reason
    //
    // Deliberately **not idempotent**: a second call is 422, not 200. Unlike
    // archive there is something to overwrite — CancelledByUserId, CancelledAtUtc
    // and the reason — so a repeat would quietly rewrite who called the meeting
    // off. See CancelBookingCommandRequest.
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<CancelBookingCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelBookingRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CancelBookingCommandRequest(id, request?.Reason),
            cancellationToken);

        return Ok(result);
    }

    // The body is optional in full, matching CancelBookingRequest — a
    // decision with no note is legal.
    public sealed record ApproveBookingRequest(string? Note = null);

    // FR-7.1-7.5, AC-5, decision 0023 inherited whole. A TenantAdmin may
    // approve any Pending booking in their tenant; an Approver only one whose
    // resource lists them (decision 0018).
    //
    // The statuses:
    //
    //   200 — Confirmed
    //   404 BookingNotFound — not reachable by this caller: another tenant's,
    //       or a resource this Approver is not assigned to, all
    //       byte-identical, never a 403 (would confirm the booking exists)
    //   422 BookingNotPending — already decided, or already cancelled
    //       (closing WP-4's loose end 1)
    //   409 SlotUnavailable, CapacityExceeded — the since-taken-slot case
    //       AC-5 names, decided under dbo.ApproveBooking's lock
    //   422 BlackoutPeriod, ResourceArchived — re-checked the same way
    //   400 ValidationFailed — an over-long note
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<ApproveBookingCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromBody] ApproveBookingRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ApproveBookingCommandRequest(id, request?.Note),
            cancellationToken);

        return Ok(result);
    }

    public sealed record RejectBookingRequest(string? Note = null);

    // Same reach as approve. Rejecting needs no capacity re-check — it
    // removes a claim rather than adding one — so there is no 409 here.
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<RejectBookingCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reject(
        Guid id,
        [FromBody] RejectBookingRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new RejectBookingCommandRequest(id, request?.Note),
            cancellationToken);

        return Ok(result);
    }
}
