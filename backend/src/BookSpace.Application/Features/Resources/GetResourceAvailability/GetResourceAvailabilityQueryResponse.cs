namespace BookSpace.Application.Features.Resources.GetResourceAvailability;

// The availability answer. Per-endpoint and in its own file, per
// docs/decisions/0015's amendment.
//
// The range is echoed back because the client sent local dates and is given UTC
// instants: without TimeZoneId and the dates it asked for, a client cannot check
// that the server read "the 8th" the way it meant it (FR-6.3, "applied
// consistently").
//
// **IsArchived is the reason this is not just a list.** An archived resource
// returns an empty interval list rather than 422 ResourceArchived (owner's call,
// 2026-09-03): FR-3.5 keeps an archived resource readable, "nothing is bookable"
// is the true answer, and a 422 on a read would be out of character with every
// other read in this API. But then "empty" has two meanings — archived, or simply
// closed all week — and this flag is what tells them apart.
//
// Deliberately *not* here: Capacity, MinDurationMinutes and MaxDurationMinutes.
// A slot picker does want them, but they are already on GET /resources/{id}, and
// the work package asks this endpoint for bookable slots. Adding them would be
// inventing response fields (CLAUDE.md §11).
public sealed record GetResourceAvailabilityQueryResponse(
    Guid ResourceId,
    string TimeZoneId,
    DateOnly FromLocalDate,
    DateOnly ToLocalDate,
    bool IsArchived,
    IReadOnlyCollection<BookableIntervalDetail> Intervals);
