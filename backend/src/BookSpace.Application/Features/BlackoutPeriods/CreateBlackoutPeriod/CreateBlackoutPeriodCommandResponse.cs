namespace BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;

// The 201 body of POST /resources/{id}/blackout-periods.
//
// Its own type rather than one shared with the list response (convention agreed
// 2026-09-01, docs/decisions/0015), and here the two are not even similar:
// CancelledBookings belongs to the moment of creation and nothing else. A list
// row cannot report it — by the time anyone reads the list, "which bookings did
// this cancel" is a question about the past that the blackout row does not
// record.
//
// CancelledBookings is empty, not absent, when the blackout hit nothing. An
// empty array says the cascade ran and found nothing; a missing field would
// leave a client guessing whether it ran at all.
public sealed record CreateBlackoutPeriodCommandResponse(
    Guid Id,
    Guid ResourceId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason,
    DateTime CreatedAtUtc,
    IReadOnlyList<CancelledBookingSummary> CancelledBookings);
