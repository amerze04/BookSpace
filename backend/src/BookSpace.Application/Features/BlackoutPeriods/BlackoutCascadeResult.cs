using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.BlackoutPeriods;

// What one run of decision 0001's cascade produced: the bookings it cancelled,
// for the response, and the notifications it minted, for the caller to state as
// inserts.
//
// A record rather than out parameters or a tuple, because both handlers use both
// halves and a named pair reads better at the call site than
// `var (a, b) = ...`. Internal — the summaries inside it are public because they
// go on the wire, but the pairing itself is an implementation detail of this
// feature.
internal sealed record BlackoutCascadeResult(
    IReadOnlyList<CancelledBookingSummary> CancelledBookings,
    IReadOnlyList<Notification> Notifications);
