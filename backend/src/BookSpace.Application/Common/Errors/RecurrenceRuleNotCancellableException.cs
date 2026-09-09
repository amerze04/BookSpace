namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (WP-5 Phase 2, FR-5.3). The series is already
// Cancelled.
//
// Deliberately not idempotent, mirroring BookingNotCancellable: a second
// cancellation would overwrite RecurrenceRules.UpdatedByUserId and
// UpdatedAtUtc with a second actor's, quietly changing the record of who
// ended the series (decision 0002 amendment 3's reasoning, one level up).
public sealed class RecurrenceRuleNotCancellableException : AppException
{
    public RecurrenceRuleNotCancellableException(Guid recurrenceRuleId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.RecurrenceRuleNotCancellable,
            $"RecurrenceRule {recurrenceRuleId} is already cancelled.")
    {
    }
}
