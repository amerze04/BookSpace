namespace BookSpace.Domain.Availability;

// What BookingEligibility.Evaluate concluded about one requested interval: it
// can be booked, or the single reason it cannot (WP-4 Phase 1a, FR-4.3).
//
// One reason, not a list, and the order the rules are applied in is fixed by
// BookingEligibility rather than left to the caller — a request that is both
// outside the schedule and inside a blackout has to produce the same answer
// every time, or a client's error handling is guessing.
//
// The four rejection members are named after the reason codes they map to
// (ReasonCodes.OutsideAvailability and so on), but this enum deliberately does
// not *hold* the codes: those are the Application layer's wire contract, and
// Domain does not know what a client sees. The mapping is one switch in the
// handler, which is also where the exception subclasses live.
public enum BookingEligibilityResult
{
    // Inside the schedule, clear of every blackout, and enough units free for
    // the whole interval. Still only an *advisory* yes on the capacity half —
    // dbo.CreateBooking re-checks it under a lock, and that check is the one
    // that counts (CLAUDE.md §4.1, AC-1).
    Eligible,

    // The interval is not wholly inside the resource's open hours (FR-3.2).
    // Partly open is not open: a booking that runs past closing is refused
    // rather than truncated.
    OutsideAvailability,

    // Open, but a blackout covers part or all of it. Decision 0001 gives a
    // blackout absolute priority over availability (FR-3.4).
    BlackoutPeriod,

    // Open and clear of blackouts, but at some instant inside the interval the
    // resource has **no** units left at all.
    SlotUnavailable,

    // Open, clear, and something is free throughout — just fewer units than
    // were asked for. Distinct from SlotUnavailable on the owner's call of
    // 2026-09-07: on a pooled resource "you asked for 3 and 2 are left" is
    // genuinely different information from "there is nothing here", while on an
    // exclusive resource (Capacity 1, decision 0005) only SlotUnavailable can
    // ever occur, because a quantity of 1 is the only one it accepts.
    CapacityExceeded,
}
