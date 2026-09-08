namespace BookSpace.Application.Common.Errors;

// The reason-code catalogue (CLAUDE.md §6, FR-4.5). A code is the client's
// contract: it branches on this string, so changing one is a breaking API
// change and adding one belongs in §6's list as well as here.
//
// Each code is paired with the ErrorKind it is thrown with, in the comment
// beside it, so the same failure never arrives as a 404 from one handler and a
// 422 from another. Kinds are documented rather than enforced in code — a
// code-to-kind map would have to be consulted at every throw site to be worth
// anything, and AppException already makes the kind explicit there.
//
// Authentication codes are deliberately NOT here: they live in
// AuthenticationFailureReason, beside the handlers that guarantee every
// credential failure looks identical (FR-2.1). Splitting them keeps that
// rationale next to the codes it constrains; the cost is two files to check
// when adding a code. See docs/decisions/0016-error-contract-and-reason-codes.md.
public static class ReasonCodes
{
    // ---- Resources (WP-3, FR-3.1 / FR-3.5) ----

    // ErrorKind.NotFound. Also the answer for another tenant's resource id:
    // after CLAUDE.md §4.2's filters the two are indistinguishable here, and
    // must stay that way (AC-4).
    public const string ResourceNotFound = "ResourceNotFound";

    // ErrorKind.RuleViolation. FR-3.5: an archived resource stays readable and
    // keeps its history, but refuses new bookings and edits.
    public const string ResourceArchived = "ResourceArchived";

    // ErrorKind.Validation. A syntactically fine string that is not a
    // recognized IANA timezone id — the shape validator cannot know the list.
    public const string InvalidTimeZone = "InvalidTimeZone";

    // ErrorKind.RuleViolation. A capacity decrease that would leave existing
    // bookings over the new limit (wp3-plan's "smaller calls").
    public const string CapacityBelowExistingBookings = "CapacityBelowExistingBookings";

    // ---- Availability windows and approvers (WP-3, FR-3.2 / FR-3.3) ----

    // ErrorKind.Conflict. Two windows on the same weekday whose hours overlap
    // are rejected rather than unioned (wp3-plan's "smaller calls"); the schema
    // constrains neither way.
    public const string OverlappingAvailabilityWindow = "OverlappingAvailabilityWindow";

    // ErrorKind.RuleViolation. FR-3.3 reads "marked RequiresApproval, with one
    // or more assigned approvers", so RequiresApproval with an empty approver
    // list is not a valid resting state.
    public const string ApproversRequired = "ApproversRequired";

    // ErrorKind.RuleViolation. The assigned user is in another tenant or lacks
    // the Approver role. Not NotFound: within the tenant the user genuinely
    // does not exist, and a cross-tenant id must not be confirmed either way.
    public const string ApproverNotEligible = "ApproverNotEligible";

    // ---- Blackout periods (WP-3, FR-3.4) ----

    // ErrorKind.RuleViolation. A blackout whose interval is entirely in the
    // past, which blocks nothing and could only reach backwards into bookings
    // that already happened. Owner's call, 2026-09-02 — see
    // BlackoutPeriodElapsedException for why the rule is about EndsAtUtc and
    // deliberately not about StartsAtUtc.
    public const string BlackoutPeriodElapsed = "BlackoutPeriodElapsed";

    // ErrorKind.NotFound. No such blackout on the resource in the path — also
    // the answer for another tenant's real blackout id, and for one that exists
    // in this tenant but belongs to a different resource. See
    // BlackoutPeriodNotFoundException for why all three collapse to one code,
    // and why it is separate from ResourceNotFound despite both being 404s.
    public const string BlackoutPeriodNotFound = "BlackoutPeriodNotFound";

    // ---- Bookings (FR-4.x, WP-4) ----
    //
    // The first four were declared by CLAUDE.md §6 in WP-3 and waited for a
    // thrower; the four after them are WP-4's own. Their exception subclasses
    // arrive with the code paths that raise them (Phase 1c), per §6: a code with
    // no subclass cannot be thrown at all.
    //
    // ApprovalRequired used to sit here and was **deleted in WP-4 Phase 1a**.
    // FR-7.1 makes a booking on an approval-gated resource enter Pending rather
    // than be refused, so nothing in the design will ever raise it, and decision
    // 0016's whole point is that the catalogue describes what the API can
    // actually return. Owner's call, 2026-09-07.

    // ErrorKind.RuleViolation. A requested interval a blackout covers.
    // Decision 0001 gives a blackout absolute priority.
    //
    // A *booking* rejection (FR-4.5), despite the blackout in the name, which is
    // why WP-3 Phase 4 does not throw it: creating a blackout is refused by
    // BlackoutPeriodElapsed, and Phase 5's availability query excludes blackout
    // time rather than raising anything. The thrower arrives with
    // dbo.CreateBooking in WP-4.
    public const string BlackoutPeriod = "BlackoutPeriod";

    // ErrorKind.Conflict. **Nothing** is free at some instant inside the
    // requested interval — the peak concurrent Quantity already equals
    // Resources.Capacity (FR-4.2, AC-1). Raised by the application pre-check and,
    // authoritatively, by dbo.CreateBooking's check under the range lock.
    public const string SlotUnavailable = "SlotUnavailable";

    // ErrorKind.Conflict. Something is free throughout, but fewer units than
    // were asked for (decision 0005's concurrent-units model).
    //
    // The split from SlotUnavailable is the owner's call of 2026-09-07: on a
    // pooled resource "you asked for 3 and 2 are left" is genuinely different
    // information, and on an exclusive resource only SlotUnavailable can occur,
    // since Capacity 1 admits no quantity but 1.
    public const string CapacityExceeded = "CapacityExceeded";

    // ErrorKind.RuleViolation. Not wholly inside the resource's availability
    // windows (FR-3.2). Partly open is not open — a booking running past closing
    // is refused, never truncated.
    public const string OutsideAvailability = "OutsideAvailability";

    // ErrorKind.NotFound. No such booking visible to this caller. One code for
    // three cases, on the same reasoning as BlackoutPeriodNotFound: the id does
    // not exist, it belongs to another tenant, or it belongs to another member
    // of this tenant and the caller is not a TenantAdmin. Reporting the third
    // separately would confirm the booking exists and leak who is using what.
    public const string BookingNotFound = "BookingNotFound";

    // ErrorKind.RuleViolation. The booking is already in a terminal status, or
    // it has already ended. The second half mirrors BlackoutPeriodElapsed: the
    // test is on EndsAtUtc, so a meeting in progress can still be called off.
    public const string BookingNotCancellable = "BookingNotCancellable";

    // ErrorKind.RuleViolation. The requested length violates the resource's
    // MinDurationMinutes or MaxDurationMinutes. Judged by
    // Resource.AllowsBookingDuration, which is the only place that rule lives.
    public const string BookingDurationOutOfRange = "BookingDurationOutOfRange";

    // ErrorKind.RuleViolation. The requested interval has **entirely** elapsed,
    // so the booking could reserve nothing. Deliberately about the end and not
    // the start, exactly as BlackoutPeriodElapsed is (decision 0019) — booking
    // the room you are already sitting in is the ordinary case.
    public const string BookingInThePast = "BookingInThePast";
}
