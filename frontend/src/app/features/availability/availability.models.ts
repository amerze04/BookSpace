// Wire types mirroring GetResourceAvailabilityQueryRequest/Response exactly
// (BookSpace.Application/Features/Resources/GetResourceAvailability/) — the
// same "one place to check when the API shape changes" convention
// resources.models.ts already established.

// DateOnly on the wire, "yyyy-MM-dd" — resource-local dates, never UTC
// (docs/decisions/0003-availability-timezone.md). Aliased rather than typed
// as plain `string` so a caller can't accidentally pass a UTC instant string
// where a local date is expected.
export type LocalDateString = string;

// GET /resources/{id}/availability query params — resourceId is a route
// segment, not a query param, so it isn't part of this type. Both dates are
// required: the backend has no "today" default (it can't know the resource's
// timezone until it loads the resource), and quantity is optional because the
// server's own default of 1 (AvailabilityCalculator.DefaultRequiredQuantity)
// should apply rather than this file guessing a second copy of it.
export interface AvailabilityParams {
  from: LocalDateString;
  to: LocalDateString;
  quantity?: number;
}

// One bookable span on the wire — BookableIntervalDetail. Instants, not local
// times: a local time on a clocks-back day names two instants, so the span
// itself has to be unambiguous even though the request was asked in local
// dates. RemainingCapacity is a floor across the whole span (decision
// `0020`'s amendment) — it holds at every instant inside the interval, for
// the `quantity` the response was asked about.
export interface BookableInterval {
  startUtc: string;
  endUtc: string;
  remainingCapacity: number;
}

// GET /resources/{id}/availability response — GetResourceAvailabilityQueryResponse.
// The range and quantity are echoed back because the client asked in local
// dates and a quantity that changes the answer; isArchived is what tells an
// archived resource's empty list apart from a resource that's simply closed
// for the whole range (decision `0020`).
export interface AvailabilityResponse {
  resourceId: string;
  timeZoneId: string;
  fromLocalDate: LocalDateString;
  toLocalDate: LocalDateString;
  quantity: number;
  isArchived: boolean;
  intervals: BookableInterval[];
}
