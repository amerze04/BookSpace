// Wire types mirroring the one-off booking DTOs exactly
// (BookSpace.Application/Features/Bookings/CreateBooking/ plus
// BookingsController.CreateBookingRequest) — the same "one place to check when
// the API shape changes" convention resources.models.ts and
// availability.models.ts already established.

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
