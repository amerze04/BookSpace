using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.RecurrenceRules.CancelSeries;

// FR-5.3 / decision 0002 reapplied one level up. POST
// /recurrence-rules/{id}/cancel, on TenantMember — the owner cancels their
// own series, a TenantAdmin cancels any series in their tenant.
//
// **Cancels the series and every occurrence still worth cancelling**, not
// just the rule: a "cancelled" series whose future occurrences kept running
// would not be cancelled in any sense a client cares about. Which occurrences
// still qualify is decision 0002's window (EndsAtUtc > now), reapplied per
// occurrence rather than decided here.
//
// Reason is optional, matching CancelBookingCommandRequest, and becomes the
// CancellationReason recorded on every occurrence it cancels.
public sealed record CancelRecurrenceSeriesCommandRequest(Guid RecurrenceRuleId, string? Reason = null)
    : IRequest<CancelRecurrenceSeriesCommandResponse>;
