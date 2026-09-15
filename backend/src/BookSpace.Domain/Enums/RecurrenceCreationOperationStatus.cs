namespace BookSpace.Domain.Enums;

// Hardening pass, item 11: the lifecycle a POST /recurrence-rules idempotency
// operation moves through. See RecurrenceCreationOperation's own header for
// why this exists at all.
public enum RecurrenceCreationOperationStatus
{
    // A series is being (or was being, and may have crashed mid-way through)
    // created against RecurrenceRuleId. A retry with the same key resumes
    // this rule rather than minting a new one.
    Creating,

    // At least one occurrence was created; RecurrenceRuleId names a real,
    // non-orphaned series. A retry with the same key replays against the
    // same rule — safe because occurrence booking ids are deterministic, so
    // an occurrence already committed is recognised rather than duplicated.
    Active,

    // The one prior attempt against this key concluded in
    // NoOccurrencesCreated and its RecurrenceRule was compensating-deleted —
    // there is nothing left to resume. RecurrenceRuleId is cleared, and a
    // retry with the same key starts over from scratch, exactly like a
    // request with no key at all.
    Failed,
}
