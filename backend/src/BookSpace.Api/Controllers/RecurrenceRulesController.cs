using BookSpace.Api.Authorization;
using BookSpace.Application.Features.RecurrenceRules.CancelSeries;
using BookSpace.Application.Features.RecurrenceRules.CreateSeries;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// FR-5.x. Thin by design (WP-2's rule), same shape as BookingsController.
//
// **A member action, on TenantMember, not an admin write.** A member books
// their own recurring slot exactly as they book a one-off one — WP-5's own
// API-surface note (wp5-plan.md §6) makes this explicit, since every other
// admin-only write in this codebase might suggest otherwise at a glance.
[ApiController]
[Route("recurrence-rules")]
[Authorize(Policy = AuthorizationPolicies.TenantMember)]
public sealed class RecurrenceRulesController : ControllerBase
{
    private readonly ISender _sender;

    public RecurrenceRulesController(ISender sender)
    {
        _sender = sender;
    }

    // Wire shape, per decision 0015: the HTTP request lives with the
    // controller, the command with the handler. No TimeZoneId field —
    // decision 0003 puts the series in the resource's own zone, so there is
    // nothing for a client to name (CreateRecurrenceSeriesCommandRequest's
    // header explains why).
    //
    // Quantity defaults here rather than in the command, matching
    // BookingsController.CreateBookingRequest — an omitted value binds to 1,
    // the smallest legal booking, instead of to 0.
    public sealed record CreateRecurrenceSeriesRequest(
        Guid ResourceId,
        RecurrenceFrequency Frequency,
        int IntervalValue,
        TimeOnly LocalStartTime,
        TimeOnly LocalEndTime,
        DateOnly StartDate,
        DateOnly? EndDate,
        int? OccurrenceCount,
        int Quantity = 1,
        string? Title = null);

    // 201 with every occurrence RecurrenceExpansion produced and what
    // happened to each — created, skipped (DST), or refused, never a bare
    // count (FR-5.4). 422 NoOccurrencesCreated carries the identical
    // breakdown when nothing was created at all.
    //
    // No CreatedAtAction: there is no GET /recurrence-rules/{id} yet (that is
    // Phase 2 territory, alongside occurrence view/cancel). The 201 status is
    // still correct — a RecurrenceRule row now exists — the header is simply
    // deferred until there is somewhere for it to point.
    [HttpPost]
    [ProducesResponseType<CreateRecurrenceSeriesCommandResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        CreateRecurrenceSeriesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CreateRecurrenceSeriesCommandRequest(
                request.ResourceId,
                request.Frequency,
                request.IntervalValue,
                request.LocalStartTime,
                request.LocalEndTime,
                request.StartDate,
                request.EndDate,
                request.OccurrenceCount,
                request.Quantity,
                request.Title),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    // The body is optional in full — a cancellation with no reason is legal
    // — so [FromBody] with a default, matching BookingsController's
    // CancelBookingRequest exactly.
    public sealed record CancelRecurrenceSeriesRequest(string? Reason = null);

    // FR-5.3 / decision 0002 reapplied. Cancels the series and every
    // occurrence it still has a live claim on (EndsAtUtc > now).
    //
    // The statuses:
    //
    //   200 — cancelled, with the ids of every occurrence it freed
    //   404 RecurrenceRuleNotFound — no such series *for this caller*:
    //       another member's, another tenant's, or nonexistent, all
    //       byte-identical (AC-4)
    //   422 RecurrenceRuleNotCancellable — already cancelled
    //   400 ValidationFailed — an over-long reason
    //
    // Deliberately **not idempotent**, exactly like the single-booking
    // cancel: a second call is 422, not 200, because there is an actor and a
    // time to overwrite.
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<CancelRecurrenceSeriesCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelRecurrenceSeriesRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CancelRecurrenceSeriesCommandRequest(id, request?.Reason),
            cancellationToken);

        return Ok(result);
    }
}
