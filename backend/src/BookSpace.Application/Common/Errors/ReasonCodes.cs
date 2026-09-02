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

    // ---- Bookings (declared by CLAUDE.md §6; first thrown in WP-4) ----
    // Listed here so the catalogue is the one place to look, and so WP-4 adds
    // throwers rather than inventing strings. No thrower exists yet: booking
    // writes go through dbo.CreateBooking, which is WP-4 work (§4.1).

    // ErrorKind.RuleViolation. A requested interval a blackout covers.
    // Decision 0001 gives a blackout absolute priority.
    //
    // A *booking* rejection (FR-4.5), despite the blackout in the name, which is
    // why WP-3 Phase 4 does not throw it: creating a blackout is refused by
    // BlackoutPeriodElapsed, and Phase 5's availability query excludes blackout
    // time rather than raising anything. The thrower arrives with
    // dbo.CreateBooking in WP-4.
    public const string BlackoutPeriod = "BlackoutPeriod";

    // ErrorKind.Conflict. The slot is taken — the stored procedure's overlap
    // check refused it (FR-4.2, AC-1).
    public const string SlotUnavailable = "SlotUnavailable";

    // ErrorKind.Conflict. Overlapping bookings' Quantity would exceed
    // Resources.Capacity (decision 0005's concurrent-units model).
    public const string CapacityExceeded = "CapacityExceeded";

    // ErrorKind.RuleViolation. Outside the resource's availability windows.
    public const string OutsideAvailability = "OutsideAvailability";

    // ErrorKind.RuleViolation. The resource requires approval, so the booking
    // cannot be confirmed directly (FR-7.x).
    public const string ApprovalRequired = "ApprovalRequired";
}
