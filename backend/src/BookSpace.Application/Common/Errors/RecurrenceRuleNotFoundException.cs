namespace BookSpace.Application.Common.Errors;

// ErrorKind.NotFound (WP-5 Phase 2, FR-5.3). No such series visible to this
// caller.
//
// One code for three cases, on BookingNotFoundException's precedent: the id
// exists nowhere, it belongs to another tenant, or it belongs to another
// member of this tenant and the caller is not a TenantAdmin. Never a 403 —
// that would confirm the series exists and leak who is running it (AC-4,
// applied within a tenant as well as across one).
public sealed class RecurrenceRuleNotFoundException : AppException
{
    public RecurrenceRuleNotFoundException(Guid recurrenceRuleId)
        : base(
            ErrorKind.NotFound,
            ReasonCodes.RecurrenceRuleNotFound,
            $"RecurrenceRule {recurrenceRuleId} was not found for the current caller.")
    {
    }
}
