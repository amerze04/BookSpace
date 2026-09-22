import { BookingSummary } from '../../booking/models/booking.models';
import { SpanLabels, instantLabel, spanLabels } from '../../availability/date/local-date';

// A queue is short by nature — a tenant with more than a page of requests
// waiting has an approver problem, not a paging problem — so this is far
// smaller than the resource list's 100. It also matches the backend's own
// default page size, which means the first request asks for exactly what the
// endpoint would have given anyway.
export const APPROVAL_QUEUE_PAGE_SIZE = 20;

// One row of the queue, already in the words the template renders. The
// component holds no formatting of its own, the same split `calendar-range.ts`
// and `availability-grid.ts` already keep: the arithmetic and the copy are
// pure functions with tests, and the component is left with fetching and state.
export interface ApprovalQueueRow {
  id: string;
  resourceName: string;
  requesterName: string;
  span: SpanLabels;
  quantityLabel: string;
  requestedAtLabel: string;
  waitingLabel: string;
  isRecurring: boolean;
}

const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'}`;
}

// "How long has this been waiting", which is the question the requested-at
// column exists to answer and the reason `createdAtUtc` was added to the list
// row at all. The absolute stamp is rendered beside this one — a relative label
// alone cannot be checked against anything, and an absolute one alone makes the
// reader do the subtraction.
//
// **A future stamp reads as "Just now" rather than as negative time.** The
// server's clock and the browser's are not the same clock, and a request
// created a few seconds ago can legitimately arrive with a `createdAtUtc` a
// moment ahead of `Date.now()`. "Waiting -1 minutes" would be the kind of
// visible nonsense that makes a reader distrust the rest of the row.
export function waitingLabel(createdAtUtc: string, nowMs: number): string {
  const elapsedMs = nowMs - Date.parse(createdAtUtc);

  if (!Number.isFinite(elapsedMs) || elapsedMs < MINUTE_MS) {
    return 'Just now';
  }
  if (elapsedMs < HOUR_MS) {
    return `Waiting ${plural(Math.floor(elapsedMs / MINUTE_MS), 'minute')}`;
  }
  if (elapsedMs < DAY_MS) {
    return `Waiting ${plural(Math.floor(elapsedMs / HOUR_MS), 'hour')}`;
  }
  return `Waiting ${plural(Math.floor(elapsedMs / DAY_MS), 'day')}`;
}

// The span reads in the **viewer's own zone**, not the resource's, matching the
// booking detail screen rather than the booking form. Decision `0003` governs
// availability — "Monday 9am" is what the room's clock says — but an approver
// is not choosing a slot, they are judging one against their own day. The
// resource's zone is one click away on `/bookings/:id`, which is where an
// approver who needs it will already be heading.
export function toQueueRow(
  booking: BookingSummary,
  viewerTimeZoneId: string,
  nowMs: number,
): ApprovalQueueRow {
  return {
    id: booking.id,
    resourceName: booking.resourceName,
    requesterName: booking.userName,
    span: spanLabels({ startUtc: booking.startsAtUtc, endUtc: booking.endsAtUtc }, viewerTimeZoneId),
    quantityLabel: plural(booking.quantity, 'unit'),
    requestedAtLabel: instantLabel(booking.createdAtUtc, viewerTimeZoneId),
    waitingLabel: waitingLabel(booking.createdAtUtc, nowMs),

    // FR-5.2: an occurrence of a series is an ordinary booking that happens to
    // carry a rule id, and decision `0007` materialized every one of them as
    // its own row. So a series arrives here as N independent requests, each
    // decided on its own — which is exactly right, and is also why the marker
    // matters: an approver seeing the same room five Mondays running should be
    // told those are one member's series rather than five unrelated asks.
    isRecurring: booking.recurrenceRuleId !== null,
  };
}
