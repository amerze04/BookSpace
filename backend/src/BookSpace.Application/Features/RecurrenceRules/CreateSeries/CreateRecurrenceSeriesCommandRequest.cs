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
//
// **IdempotencyKey (hardening pass, item 11), from the Idempotency-Key
// request header, not the body** — an HTTP idempotency key describes the
// request attempt, not the resource being created, the same distinction the
// correlation id already makes. Optional: a client that never retries (or
// retries by accepting the risk) gets today's behaviour unchanged — a fresh
// RecurrenceRule and a fresh attempt at every occurrence. A client that
// supplies one gets a request that is safe to retry after a crash or a lost
// response, because CreateRecurrenceSeriesCommandRequestHandler resolves the
// same key to the same RecurrenceRule every time.
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
    string? Title,
    string? IdempotencyKey = null)
    : IRequest<CreateRecurrenceSeriesCommandResponse>;
