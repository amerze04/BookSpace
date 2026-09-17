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
