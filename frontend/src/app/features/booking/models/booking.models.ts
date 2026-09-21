// Wire types mirroring the one-off booking DTOs exactly
// (BookSpace.Application/Features/Bookings/CreateBooking/, ListBookings/,
// GetBooking/, CancelBooking/, plus BookingsController's own request records) —
// the same "one place to check when the API shape changes" convention
// resources.models.ts and availability.models.ts already established.
//
// Phase 3 needed only the create half; Phase 4 (My Bookings) adds the read and
// cancel halves below. They stay in this one file rather than a second
// my-bookings.models.ts because they are the *same resource's* DTOs — a client
// splitting them by which screen happened to need them first would be filing by
// accident of history rather than by contract.

// BookSpace.Domain.Enums.BookingStatus, in its own declared order. Serializes
// as its name, not an ordinal (Program.cs registers JsonStringEnumConverter on
// both the MVC and the minimal-API JSON options).
//
// The full domain is declared even though POST /bookings can only ever answer
// Pending or Confirmed: the same type is what GET /bookings will read back in
// Phase 4, where every value is reachable, and a type that quietly omitted
// Cancelled would then have to be widened rather than reused.
export const BOOKING_STATUSES = [
  'Pending',
  'Confirmed',
  'Rejected',
  'Cancelled',
  'Completed',
  'NoShow',
] as const;
export type BookingStatus = (typeof BOOKING_STATUSES)[number];

// Bookings.Title NVARCHAR(200), restated from
// CreateBookingCommandRequestValidator.MaxTitleLength so the form can bound
// the input rather than letting a 400 be the first thing that says so.
// CreateRecurrenceSeriesCommandRequestValidator declares its own copy of the
// same 200 on the backend; the two have never differed, so this is one
// constant here rather than two (see recurrence.models.ts).
export const MAX_BOOKING_TITLE_LENGTH = 200;

// The approval half of a create response — BookingApprovalDetail. Present only
// when the resource required approval (FR-7.1), null otherwise, which is
// exactly how the screen tells a Pending outcome from a Confirmed one.
//
// expiresAtUtc is nullable inside it for a different reason than the record
// itself being nullable: a tenant that has set no ApprovalExpiryHours leaves
// requests pending indefinitely (FR-7.4), a legitimate configuration rather
// than a missing value — so "no expiry" has to render as its own sentence, not
// as a blank date.
export interface BookingApprovalDetail {
  approvalRequestId: string;
  expiresAtUtc: string | null;
}

// POST /bookings body — BookingsController.CreateBookingRequest. No userId and
// no orgId: both come from the token, so neither is a field a client can send.
//
// **Instants, not local wall clock**, and specifically instants this client got
// *from* the availability response rather than ones it computed: the backend
// takes UTC here precisely because a one-off booking is picked out of
// GET /resources/{id}/availability's own UTC answer
// (CreateBookingCommandRequest's header, and wp7-plan.md Phase 3's
// "manual one-off date/time entry is dropped" call).
//
// Two shape rules the validator enforces and the caller therefore has to
// respect — both verified against the running API, not assumed:
//   - each instant must carry a zone designator ("...Z"); a bare
//     local-looking timestamp is Unspecified and is refused;
//   - neither may carry fractional seconds — the columns are datetime2(0), so
//     a sub-second value would be rounded on write and the response would then
//     disagree with the row read back (CLAUDE.md §4.3). A real
//     `2026-09-21T13:00:00.500Z` comes back 400 ValidationFailed with
//     errors.StartsAtUtc, which is why the submit step truncates to whole
//     seconds on the way out rather than trusting its inputs.
//
// quantity is sent explicitly rather than omitted to let the server's default
// of 1 apply — unlike AvailabilityParams.quantity. The booking form always
// knows the number it is asking for (1 for an exclusive resource, per decision
// 0005), so there is no "unset" case here for a default to cover.
export interface CreateBookingRequest {
  resourceId: string;
  startsAtUtc: string;
  endsAtUtc: string;
  quantity: number;
  title: string | null;
}

// POST /bookings 201 — CreateBookingCommandResponse. More than an echo of the
// request, and `status` is the field that makes it so: a booking on an
// approval-gated resource comes back Pending and the member has to be told at
// the moment of booking (FR-7.1).
//
// Deliberately no remainingCapacity, and the UI must not invent one: that
// record's own header explains why — a number a client might act on invites
// the check-then-act race dbo.CreateBooking exists to prevent. The
// availability endpoint is where that question belongs.
export interface CreateBookingResponse {
  id: string;
  resourceId: string;
  userId: string;
  startsAtUtc: string;
  endsAtUtc: string;
  quantity: number;
  title: string | null;
  status: BookingStatus;
  createdAtUtc: string;
  approval: BookingApprovalDetail | null;
}

// ---------------------------------------------------------------------------
// Phase 4 — the read and cancel halves.
// ---------------------------------------------------------------------------

// The `sort` whitelist BookingSortFields declares server-side (decision 0015),
// spelled the way each field appears in the response JSON so a sort control can
// only ever offer something the reader can actually see. Anything not on this
// list is a 400, not a silently ignored parameter.
//
// Deliberately short on the backend's own reasoning: quantity and title are
// omitted because nobody browses a schedule by either.
export const BOOKING_SORT_FIELDS = ['startsAtUtc', 'createdAtUtc', 'status'] as const;
export type BookingSortField = (typeof BOOKING_SORT_FIELDS)[number];

// `name` ascending, `-name` descending — SortOption.TryParse's own grammar,
// expressed as a type rather than as a `string` the caller has to get right.
// ListResourcesParams.sort is a bare `string` (Phase 1); this is the stricter
// version, so a typo is a compile error here rather than a 400 at runtime.
export type BookingSort = BookingSortField | `-${BookingSortField}`;

// BookSpace.Application.Features.Bookings.BookingScope, spelled the way the
// query string carries it. ASP.NET Core binds an enum query value with
// Enum.TryParse, which is case-insensitive, so lowercase is what goes on the
// wire and lowercase is what this type declares — no casing conversion at the
// call site, and no second spelling to keep in step.
//
// `own` is the endpoint's own default, so a caller wanting it omits the
// parameter rather than spelling it out — the project's rule that an omitted
// filter is genuinely absent from the URL rather than sent as a default this
// client invented (decision 0015). It stays in the type because the enum has
// it: this mirrors a backend type rather than enumerating only the values one
// screen happens to need, and a caller that does set it explicitly gets it
// sent verbatim, like every other parameter here.
export const BOOKING_SCOPES = ['own', 'tenant'] as const;
export type BookingScope = (typeof BOOKING_SCOPES)[number];

// GET /bookings query parameters — BookingsController.ListBookingsRequest.
//
// **from/to are an overlap filter, not a containment one** (the query record's
// own header): a booking counts if any part of it falls inside the window, so a
// member asking about next week still sees the booking that started this Friday
// and runs into it.
//
// Two rules the validator enforces, which is why the caller cannot treat these
// as free-form strings: each instant must carry a zone designator (an
// Unspecified DateTime is refused, exactly as on POST /bookings), and `to` must
// be strictly after `from` when both are sent — equal is refused too, since a
// zero-width window overlaps nothing and so could only ever read as "you have
// no bookings".
//
// **`scope` arrives in WP-7 Phase 6; `userId` deliberately does not.** This
// block used to say both were absent, and half of that is now out of date —
// the approval queue is the path that exercises `scope`, and it is here.
//
// The two are not symmetrical, which is why only one of them landed:
//
//   - `scope=tenant` may be sent by a **TenantAdmin or an Approver**
//     (ListBookingsQueryRequestValidator was widened for exactly this queue in
//     WP-5 Phase 3, decision 0018). The server then narrows the rows itself
//     via ApprovalReach — unrestricted for an admin, assigned-resources-only
//     for an approver, and empty for a plain member, who therefore gets an
//     empty page rather than a 403. **The client does not branch on role and
//     must not start**: it asks the same question and the answer is already
//     correctly scoped.
//   - `userId` stays TenantAdmin-only and stays out. Nothing in WP-7 filters a
//     queue by one member, and a parameter a member's token can only ever be
//     400'd for sending is not surface worth carrying.
//
// Sending `userId` together with a non-`Own` scope is refused outright rather
// than given a precedence rule, so the two could not be combined even if both
// were here.
export interface ListBookingsParams {
  from?: string;
  to?: string;
  status?: BookingStatus;
  resourceId?: string;
  page?: number;
  pageSize?: number;
  sort?: BookingSort;
  scope?: BookingScope;
}

// One row of GET /bookings — ListBookingsQueryResponse. A summary, not the
// whole aggregate, and the omissions are load-bearing for the UI rather than
// incidental: **none of the cancellation fields are here**, so the list can
// show that a booking is Cancelled but not who cancelled it or why. That is
// correct (a page of twenty rows should not carry columns null on all of them)
// and it is why "cancelled by an administrator" is a detail-screen fact, one
// click deeper — flagged in wp7-plan.md's Phase 4 section rather than
// discovered at review.
//
// resourceName and userName are denormalized onto the row on purpose (owner's
// call, 2026-09-08 / WP-5 Phase 3): a member's list spans resources by
// definition, so ids alone would force a fetch per row to render anything a
// person could read.
//
// userName is mapped but not rendered on a `scope=own` read — every row there
// is the viewer's own, so printing their own name on each is noise. **Phase 6's
// queue is its first real reader**, and the first caller of this endpoint who
// does not already know whose booking each row is.
//
// createdAtUtc is new in WP-7 Phase 6 (added to the backend row on the owner's
// call, 2026-09-21) and is the queue's requested-at column — "how long has this
// been waiting", which is what makes a queue a queue rather than a list. It is
// the booking's own stamp, **not** the approval request's `requestedAtUtc`:
// that one is on the detail read only, so using it would force a
// GET /bookings/{id} per visible row. The two are written in the same unit of
// work, so nothing real is lost.
//
// recurrenceRuleId is what makes a row a series occurrence (FR-5.2). It is set
// on every occurrence of a series and null on a one-off booking; the list uses
// it for the recurrence badge, and the detail screen for the this-occurrence-or
// -the-whole-series cancel choice.
export interface BookingSummary {
  id: string;
  resourceId: string;
  resourceName: string;
  userId: string;
  userName: string;
  recurrenceRuleId: string | null;
  startsAtUtc: string;
  endsAtUtc: string;
  quantity: number;
  title: string | null;
  status: BookingStatus;
  createdAtUtc: string;
}

// BookSpace.Domain.Enums.ApprovalDecision, in its own declared order.
//
// Withdrawn is not a person's judgment: it is what happens to a still-Pending
// request whose booking stopped existing to decide on — cancelled directly, by
// a blackout cascade (decision 0001), or by a whole-series cancellation. It
// matters to this phase specifically, because cancelling a Pending booking is
// exactly how a member produces one.
export const APPROVAL_DECISIONS = [
  'Pending',
  'Approved',
  'Rejected',
  'Expired',
  'Withdrawn',
] as const;
export type ApprovalDecision = (typeof APPROVAL_DECISIONS)[number];

// The approval section of a detail read — GetBookingApprovalDetail. Present
// only when the resource required approval, null otherwise.
//
// **Not the same type as BookingApprovalDetail above**, and not shared with it,
// per decision 0015's per-endpoint rule — here they genuinely diverge rather
// than merely coinciding. A freshly created booking's approval is Pending by
// construction, so the create response needs only the id and the expiry; this
// one is read at any time afterwards, so it also carries the outcome once made
// (decision, decider, when) and the approver's note, plus requestedAtUtc, since
// "how long has this been waiting" is a question only a detail read is asked.
//
// The row is never withdrawn from the response once decided, so a client
// reading this sees the outcome rather than merely that a request was once open.
export interface BookingDetailApproval {
  approvalRequestId: string;
  requestedAtUtc: string;
  expiresAtUtc: string | null;
  decision: ApprovalDecision;
  decidedByUserId: string | null;
  decidedAtUtc: string | null;
  note: string | null;
}

// GET /bookings/{id} — GetBookingQueryResponse. Everything on the list row plus
// the fields that only become interesting after creation.
//
// **404 for anything this caller may not see, never 403**: another member's
// booking, another tenant's booking and an id that exists nowhere are
// byte-identical (AC-4's cross-tenant rule applied within one tenant), which is
// why the detail screen's not-found copy deliberately does not distinguish
// "doesn't exist" from "isn't yours".
//
// The cancellation trio is three fields and **three** readings, not two, and
// getting them apart is the whole reason the detail screen exists:
//   - cancelledByUserId === userId — the member cancelled it themselves;
//   - cancelledByUserId !== userId — an administrator cancelled it
//     (decision 0002 records the actor distinctly precisely so this is visible);
//   - cancelledByUserId === null with a reason — a blackout cancelled it
//     (Booking.CancelForBlackout leaves the actor null on purpose, and the
//     reason is decision 0019's text snapshot naming the blackout).
//
// checkedInAtUtc is on the wire and is null in practice today — nothing writes
// it until the no-show job lands (FR-4.x check-in).
export interface BookingDetail {
  id: string;
  resourceId: string;
  resourceName: string;
  userId: string;
  userName: string;
  recurrenceRuleId: string | null;
  startsAtUtc: string;
  endsAtUtc: string;
  quantity: number;
  title: string | null;
  status: BookingStatus;
  checkedInAtUtc: string | null;
  cancelledByUserId: string | null;
  cancelledAtUtc: string | null;
  cancellationReason: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  approval: BookingDetailApproval | null;
}

// Bookings.CancellationReason NVARCHAR(300), restated from
// CancelBookingCommandRequestValidator.MaxReasonLength so the form can bound
// the input rather than letting a 400 be the first thing that says so. The
// series cancel declares its own copy of the same 300 on the backend; the two
// have never differed, so this is one constant rather than two.
export const MAX_CANCELLATION_REASON_LENGTH = 300;

// POST /bookings/{id}/cancel body — BookingsController.CancelBookingRequest.
// The body is optional in full ("the meeting is off" is often all there is to
// say), and there is no actor field: who cancelled comes from the token, so an
// admin cannot attribute their own cancellation to the booking's owner.
export interface CancelBookingRequest {
  reason: string | null;
}

// POST /bookings/{id}/cancel 200 — CancelBookingCommandResponse.
//
// **The interval is on the wire even though the booking no longer holds it**,
// and that is the point of the reply rather than an echo: it names the span
// that just became bookable again, which re-reading the availability endpoint
// could not answer (that says what is free *now*, not what this action freed).
// It is what lets the screen update from the response instead of blind-
// refetching.
//
// cancelledByUserId and cancelledAtUtc are non-nullable here, unlike on the
// detail read: a response *to* a cancellation always has a real person behind
// it. The blackout cascade's null actor never reaches this endpoint.
//
// **Deliberately not idempotent** — a second call is 422 BookingNotCancellable,
// not a second 200, because there is something to overwrite: repeating it would
// quietly rewrite who called the meeting off. That is why the cancel action
// offers no retry on an unknown outcome, the same rule POST /bookings follows
// for a different reason (§7's idempotency gap).
export interface CancelBookingResponse {
  id: string;
  resourceId: string;
  userId: string;
  startsAtUtc: string;
  endsAtUtc: string;
  quantity: number;
  title: string | null;
  status: BookingStatus;
  cancelledByUserId: string;
  cancelledAtUtc: string;
  cancellationReason: string | null;
}

// ---------------------------------------------------------------------------
// Phase 6 — the approval decisions.
// ---------------------------------------------------------------------------

// ApprovalRequests.Note NVARCHAR(500), restated from
// ApproveBookingCommandRequestValidator.MaxNoteLength so the form can bound the
// input rather than letting a 400 be the first thing that says so.
//
// **A different number from the two limits beside it**, and the reason it gets
// its own constant rather than reusing one that merely looks similar: a
// cancellation reason is 300 (MAX_CANCELLATION_REASON_LENGTH) and a title is
// 200 (MAX_BOOKING_TITLE_LENGTH). Reject declares its own copy of the same 500
// on the backend and the two have never differed, so this is one constant here
// rather than two.
export const MAX_DECISION_NOTE_LENGTH = 500;

// POST /bookings/{id}/approve body — BookingsController.ApproveBookingRequest.
// The body is optional in full server-side (a decision with no note is legal);
// this client always sends one, with `note: null` when there is nothing to say,
// so there is a single request shape to test and reason about. Same rule
// CancelBookingRequest already follows.
//
// No actor field, for the same reason cancel has none: who decided comes from
// the token, so an approver cannot attribute their decision to someone else by
// editing a body.
export interface ApproveBookingRequest {
  note: string | null;
}

// POST /bookings/{id}/reject body — BookingsController.RejectBookingRequest.
// Structurally identical to the approve body and deliberately not shared with
// it, per decision 0015's per-endpoint rule: these are two different decisions
// with different futures, and the backend keeps them as two records for the
// same reason.
export interface RejectBookingRequest {
  note: string | null;
}

// POST /bookings/{id}/approve 200 — ApproveBookingCommandResponse.
//
// `status` is what makes this more than an echo: an approved booking comes back
// **Confirmed**, which is the fact the queue acts on to drop the row. It is
// read from the response rather than assumed, because the approval re-runs the
// capacity check under dbo.ApproveBooking's lock (AC-5) and a success is
// therefore a real outcome rather than a formality.
//
// decidedByUserId is the caller, always — the endpoint takes no actor. It is on
// the wire so a screen can render "approved by you" without a second lookup.
export interface ApproveBookingResponse {
  id: string;
  status: BookingStatus;
  decidedByUserId: string;
  decidedAtUtc: string;
}

// POST /bookings/{id}/reject 200 — RejectBookingCommandResponse. Its `status`
// comes back **Rejected**.
//
// Kept separate from ApproveBookingResponse rather than aliased, matching the
// backend's own split and decision 0015's per-endpoint rule. The two are the
// same shape today; they are not the same contract, and an approved booking's
// response is the one that would grow an approval-detail field first.
export interface RejectBookingResponse {
  id: string;
  status: BookingStatus;
  decidedByUserId: string;
  decidedAtUtc: string;
}
