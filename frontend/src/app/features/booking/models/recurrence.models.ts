// Wire types mirroring the recurring-series DTOs exactly
// (BookSpace.Application/Features/RecurrenceRules/CreateSeries/,
// RecurrenceOccurrenceReport, and RecurrenceRulesController.CreateRecurrenceSeriesRequest).
//
// **This endpoint is not symmetric with POST /bookings, and the asymmetry is
// the point** (wp7-plan.md Phase 3, "the contract this phase actually
// consumes"): a one-off booking names UTC instants picked out of the
// availability response, while a series names *resource-local wall clock* and
// lets the handler resolve what instant each occurrence is, per decision
// `0003`, using the resource's own TimeZoneId. There is deliberately no
// timeZoneId field to send.

import { LocalDateString } from '../../availability/models/availability.models';
import { ProblemDetails } from '../../../core/http/problem-details';

// BookSpace.Domain.Enums.RecurrenceFrequency. Weekly takes no weekday field —
// the weekday comes from startDate — which is a real misreading risk the form
// (step 6) has to head off in copy rather than something this type can express.
export const RECURRENCE_FREQUENCIES = ['Daily', 'Weekly', 'Monthly'] as const;
export type RecurrenceFrequency = (typeof RECURRENCE_FREQUENCIES)[number];

// TimeOnly on the wire. System.Text.Json's default TimeOnly format is
// "HH:mm:ss", the same shape AvailabilityWindowDetail.opensAt already reads
// back. Aliased rather than plain `string` for the same reason
// LocalDateString is: so a UTC instant can't be passed where a resource-local
// wall-clock time is expected.
//
// Confirmed against the running API rather than assumed: "03:00:00" is
// accepted, and so is the shorter "03:00" — but this client always sends the
// full three-part form, so a change in what the shorter one means (or stops
// meaning) can't affect it.
export type LocalTimeString = string;

// RecurrenceOccurrenceReportStatus. FR-5.4: a series-creation response names
// every date the expansion produced and says what happened to it, never a bare
// count.
//   Created                 — dbo.CreateBooking accepted it; bookingId is set.
//   SkippedSpringForwardGap — decision 0008: the local start or end fell in a
//                             clocks-forward gap. Neither bookingId nor
//                             reasonCode is set: there was nothing to refuse,
//                             only nothing to create.
//   Refused                 — a rule or a capacity race refused it; reasonCode
//                             is set, bookingId is not.
export const RECURRENCE_OCCURRENCE_REPORT_STATUSES = [
  'Created',
  'SkippedSpringForwardGap',
  'Refused',
] as const;
export type RecurrenceOccurrenceReportStatus =
  (typeof RECURRENCE_OCCURRENCE_REPORT_STATUSES)[number];

// One entry of the per-occurrence breakdown. reasonCode is a `ReasonCodes`
// value (CLAUDE.md §6) — the same catalogue a one-off rejection uses, so the
// report renders through one map rather than its own vocabulary.
export interface RecurrenceOccurrenceReport {
  occurrenceDate: LocalDateString;
  status: RecurrenceOccurrenceReportStatus;
  bookingId: string | null;
  reasonCode: string | null;
}

// CK_RecurrenceRules_EndCondition, expressed in the type system: exactly one
// of endDate or occurrenceCount, never both and never neither. The `?: never`
// arms are what make the wrong combination a compile error here rather than a
// 400 the form could have prevented — the same "mirror the server's rule, do
// not invent a second one" approach Phase 2 took with
// AvailabilityQueryRules.MaxRangeDays.
export type RecurrenceEndCondition =
  | { endDate: LocalDateString; occurrenceCount?: never }
  | { endDate?: never; occurrenceCount: number };

// POST /recurrence-rules body — CreateRecurrenceSeriesRequest. The
// idempotency key is **not** here: it travels as an `Idempotency-Key` header,
// because it describes the HTTP attempt rather than the series being created
// (see RecurrenceRulesService.create).
//
// quantity is sent explicitly, same reasoning as CreateBookingRequest's.
export type CreateRecurrenceSeriesRequest = {
  resourceId: string;
  frequency: RecurrenceFrequency;
  intervalValue: number;
  localStartTime: LocalTimeString;
  localEndTime: LocalTimeString;
  startDate: LocalDateString;
  quantity: number;
  title: string | null;
} & RecurrenceEndCondition;

// POST /recurrence-rules 201 — CreateRecurrenceSeriesCommandResponse. Returned
// when at least one occurrence was created; see NoOccurrencesCreatedProblem
// below for the all-refused case, which carries the identical breakdown.
export interface CreateRecurrenceSeriesResponse {
  recurrenceRuleId: string;
  occurrences: RecurrenceOccurrenceReport[];
}

// The 422 counterpart. NoOccurrencesCreatedException puts the same breakdown
// in AppException.Extensions and GlobalExceptionHandler copies those onto
// ProblemDetails.Extensions, so `occurrences` arrives as a **top-level key on
// the problem response** — beside reasonCode, not nested under it.
//
// Verified against the running API rather than inferred, since a boxed value
// inside ProblemDetails.Extensions is serialized by its runtime type and could
// plausibly have come out PascalCase or with an ordinal status. A real
// response to an all-refused weekly series:
//
//   {"title":"The request was rejected by a rule.","status":422,
//    "reasonCode":"NoOccurrencesCreated",
//    "occurrences":[{"occurrenceDate":"2026-09-20","status":"Refused",
//                    "bookingId":null,"reasonCode":"OutsideAvailability"}, ...],
//    "correlationId":"..."}
//
// — camelCase members, the status as its name, and the date as "yyyy-MM-dd",
// identical to the 201's own `occurrences`. That is what lets one renderer
// (step 7) serve both.
export interface NoOccurrencesCreatedProblem extends ProblemDetails {
  occurrences: RecurrenceOccurrenceReport[];
}

// ---------------------------------------------------------------------------
// Phase 4 — cancelling a whole series (FR-5.3).
// ---------------------------------------------------------------------------

// POST /recurrence-rules/{id}/cancel body —
// RecurrenceRulesController's own cancel request. Optional in full, matching
// the single-booking cancel, and the reason becomes the CancellationReason
// recorded on *every* occurrence it cancels — which is why the backend
// validates it once rather than once per occurrence.
export interface CancelRecurrenceSeriesRequest {
  reason: string | null;
}

// POST /recurrence-rules/{id}/cancel 200 —
// CancelRecurrenceSeriesCommandResponse.
//
// **cancelledBookingIds, not a bare count**, and the UI owes it more than a
// number: this is the exact set of occurrences that were actually freed, so a
// screen knows precisely what it can stop showing as booked without re-reading
// GET /bookings (which would answer a different question — what is booked
// *now*, not what this action just released). The single-booking cancel's freed
// interval is the same idea one level down.
//
// **Only occurrences with EndsAtUtc > now are cancelled**; past ones survive,
// decision 0002's cancellation window reapplied per occurrence. So this list is
// routinely shorter than the series is long, and the UI has to say what
// "remaining" means *before* the member confirms rather than explaining the
// discrepancy afterwards.
export interface CancelRecurrenceSeriesResponse {
  recurrenceRuleId: string;
  cancelledByUserId: string;
  cancelledAtUtc: string;
  cancelledBookingIds: string[];
}
