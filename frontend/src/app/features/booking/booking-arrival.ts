import { toWholeSecondUtcIso } from '../availability/local-date';

// How a selected slot travels from the availability screen to the booking
// screen: **query parameters**, not router state.
//
// Router state (the History API's per-entry state object — ASP.NET's TempData
// is the closest analogue) was the first implementation and was replaced on
// 2026-09-17 at the owner's decision. It survives a reload, but it is invisible
// in the URL and cannot be shared, bookmarked, or opened in a new tab, so
// "here's the slot, book it" was not expressible as a link and neither the
// owner nor a test could see what the screen had been handed. Query parameters
// are visible, shareable, survive a reload, and work with back/forward.
//
// **The URL is not a trust boundary, and nothing here pretends otherwise.**
// Anyone can hand-edit these values — but `dbo.CreateBooking` re-checks
// availability, blackouts and peak capacity under its own lock at submit time
// regardless of what the client pre-filled (CLAUDE.md §4.1), so an edited URL
// earns an ordinary `SlotUnavailable`/`OutsideAvailability` rejection rather
// than a bad booking. This module's validation exists to keep a malformed URL
// from becoming a malformed *request*, not to secure anything.
//
// Both halves of the contract live here — the writer (`buildBookingQueryParams`,
// called by the availability screen) and the reader (`parseBookingSelection`) —
// so the parameter names exist in exactly one place and cannot drift apart.
// That is also why the availability feature imports from the booking feature
// and not the other way round: the consumer owns the contract.

export interface BookingSelection {
  startUtc: string;
  endUtc: string;
  quantity: number;
}

const START_PARAM = 'startUtc';
const END_PARAM = 'endUtc';
const QUANTITY_PARAM = 'quantity';

// Structurally satisfied by Angular's own ParamMap, without this module having
// to depend on @angular/router — same "keep the pure logic Angular-free"
// reasoning availability-grid.ts and local-date.ts already follow.
export interface QueryParamSource {
  get(name: string): string | null;
}

// The instants are written whole-second and UTC-suffixed, not raw ISO with
// milliseconds: `CreateBookingCommandRequestValidator` refuses fractional
// seconds outright (the columns are datetime2(0), so a sub-second value would
// be rounded on write and the response would then disagree with the row read
// back — CLAUDE.md §4.3), and a URL is read by people, where ".000Z" is noise.
export function buildBookingQueryParams(selection: BookingSelection): Record<string, string> {
  return {
    [START_PARAM]: toWholeSecondUtcIso(selection.startUtc),
    [END_PARAM]: toWholeSecondUtcIso(selection.endUtc),
    [QUANTITY_PARAM]: String(selection.quantity),
  };
}

// Validated, never cast. A missing or malformed parameter resolves to `null`,
// which the booking screen renders as its ordinary "pick a time first" state —
// the same state a bare deep link produces — rather than as an error.
//
// The rules mirror the server's, so the form never builds a request the API
// would only refuse:
//   - both instants must carry a zone designator (`CarryAZone` in
//     CreateBookingCommandRequestValidator). Without this check a
//     zone-less `2026-09-24T13:15:00` would be parsed by Date as the
//     *viewer's* local time and silently shift the booking by their offset —
//     exactly the class of bug decision `0003` and CLAUDE.md §4.3 exist to
//     keep out of this client;
//   - the interval must be real and forward (CK_Bookings_Interval);
//   - quantity must be a positive integer (CK_Bookings_Quantity).
//
// What survives is normalized to whole-second UTC, so an offset form
// (`…T15:15:00+02:00`) and a `Z` form of the same instant produce an identical
// selection, and step 3's submit has nothing left to truncate.
const ZONE_DESIGNATOR = /(?:Z|[+-]\d{2}:?\d{2})$/;

export function parseBookingSelection(params: QueryParamSource): BookingSelection | null {
  const startUtc = params.get(START_PARAM);
  const endUtc = params.get(END_PARAM);
  const quantity = params.get(QUANTITY_PARAM);

  if (startUtc === null || endUtc === null || quantity === null) {
    return null;
  }

  if (!ZONE_DESIGNATOR.test(startUtc) || !ZONE_DESIGNATOR.test(endUtc)) {
    return null;
  }

  const startMs = Date.parse(startUtc);
  const endMs = Date.parse(endUtc);
  if (Number.isNaN(startMs) || Number.isNaN(endMs) || endMs <= startMs) {
    return null;
  }

  // Number(), not parseInt(): parseInt('2 rooms') is 2, which would accept a
  // value the API never sent. Number('2 rooms') is NaN.
  const parsedQuantity = Number(quantity);
  if (!Number.isInteger(parsedQuantity) || parsedQuantity < 1) {
    return null;
  }

  return {
    startUtc: toWholeSecondUtcIso(startUtc),
    endUtc: toWholeSecondUtcIso(endUtc),
    quantity: parsedQuantity,
  };
}
