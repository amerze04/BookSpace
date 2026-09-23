// Wire types mirroring GET /resources/{id}/blackout-periods
// (BookSpace.Application/Features/BlackoutPeriods/ListBlackoutPeriods/).
//
// Read by the availability screen, not by an admin screen: that endpoint sits
// on `TenantMember` deliberately — its own handler comment says "a member
// choosing when to book needs to see when a resource is blacked out, the same
// reason the read detail carries the availability windows". Blackout *writes*
// are TenantAdmin-only and are not part of WP-7 at all (wp7-plan.md §7).

export interface BlackoutPeriodSummary {
  id: string;
  resourceId: string;
  startsAtUtc: string;
  endsAtUtc: string;
  reason: string | null;
  createdAtUtc: string;
}

// `from`/`to` are an **overlap** filter, not a containment one (the query's
// own comment): a blackout counts if any part of it falls in the window, so
// maintenance that started last week and runs through Tuesday is returned for
// a window that only covers Tuesday. That is exactly what this screen needs —
// a blackout straddling the edge of the visible range still blocks the days
// inside it.
export interface ListBlackoutPeriodsParams {
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
  sort?: string;
}

// ---- Write contracts (admin console phase 6) ----
//
// The note above is no longer true about writes: phase 6 built the admin screen
// that uses them. The read stays on TenantMember and these stay TenantAdmin.

// One booking decision `0001`'s cascade cancelled — CancelledBookingSummary.
//
// **On the wire because the cascade is the part of a blackout write an admin
// cannot predict**, and a silent side effect that cancels other people's
// meetings is the worst kind. `recurrenceRuleId` is non-null when the booking
// was an occurrence of a series: the series itself is untouched, only that
// occurrence.
export interface CancelledBooking {
  bookingId: string;
  userId: string;
  startsAtUtc: string;
  endsAtUtc: string;
  recurrenceRuleId: string | null;
}

// POST and PUT bodies. `reason` is optional — the column is nullable, because
// "the room is unavailable" is sometimes all an admin can say.
//
// The instants **must carry a zone**: the validator refuses a bare local-looking
// timestamp outright rather than guessing one, so these are always full ISO
// strings with a trailing Z.
export interface BlackoutPeriodInput {
  startsAtUtc: string;
  endsAtUtc: string;
  reason: string | null;
}

// The 201 body of POST. `cancelledBookings` is empty, not absent, when the
// cascade hit nothing — an empty array says it ran and found none, where a
// missing field would leave a client guessing whether it ran at all.
export interface CreateBlackoutPeriodResponse {
  id: string;
  resourceId: string;
  startsAtUtc: string;
  endsAtUtc: string;
  reason: string | null;
  createdAtUtc: string;
  cancelledBookings: CancelledBooking[];
}

// The 200 body of PUT. `cancelledBookings` names only what *this* request
// cancelled — bookings the previous interval had already cancelled were
// reported when it happened, and repeating them would read as a fresh
// cancellation of meetings that have been off the calendar for a week.
export interface UpdateBlackoutPeriodResponse extends CreateBlackoutPeriodResponse {
  updatedAtUtc: string;
}
