using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.RecurrenceRules.CreateSeries;

// FR-5.1: a member creates a recurring series for a resource. POST
// /recurrence-rules, on TenantMember — a member action, on the same footing
// as POST /bookings, not an admin write.
//
// **Local wall-clock times and dates, never instants.** Decision 0003 puts
// availability — and therefore a recurring schedule — in the resource's own
// timezone, so the client names *when* the series runs and this handler
// resolves *what instant that is*, per occurrence, via the resource's own
// TimeZoneId. There is deliberately no TimeZoneId field on this request:
// nothing here for a client to get wrong (wp5-plan.md §5.1, smaller call 1).
// That is also why WP-4's POST /bookings takes instants and this does not —
// a one-off booking is picked out of the availability endpoint's UTC answer,
// while a series is a standing local-time rule.
//
// No RecurrenceRuleId, no UserId: the id is minted by the handler and the
// actor comes from the token, exactly as POST /bookings does.
public sealed record CreateRecurrenceSeriesCommandRequest(
    Guid ResourceId,
    RecurrenceFrequency Frequency,
    int IntervalValue,
    TimeOnly LocalStartTime,
    TimeOnly LocalEndTime,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? OccurrenceCount,
    int Quantity,
    string? Title)
    : IRequest<CreateRecurrenceSeriesCommandResponse>;
