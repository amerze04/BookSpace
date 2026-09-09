namespace BookSpace.Application.Features.RecurrenceRules.CreateSeries;

// POST /recurrence-rules, 201. Per-endpoint and in its own file, per decision
// 0015's amendment.
//
// **Occurrences, not a count.** FR-5.4 requires collisions and blackout
// conflicts to be surfaced at creation and never dropped silently, and decision
// 0007 makes creation best-effort per occurrence — so the response has to name
// every date RecurrenceExpansion produced and say what happened to it. This is
// returned only when at least one occurrence was created; an all-refused
// series carries the identical shape on NoOccurrencesCreatedException instead
// (wp5-plan.md §5.1, shape question 2).
public sealed record CreateRecurrenceSeriesCommandResponse(
    Guid RecurrenceRuleId,
    IReadOnlyList<RecurrenceOccurrenceReport> Occurrences);
