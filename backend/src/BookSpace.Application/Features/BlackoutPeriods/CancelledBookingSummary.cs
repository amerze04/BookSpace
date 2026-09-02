namespace BookSpace.Application.Features.BlackoutPeriods;

// One booking decision 0001's cascade cancelled.
//
// On the wire because the cascade is the part of a blackout write an admin
// cannot predict, and a silent side effect that cancels other people's meetings
// is the worst kind. PRD AC-2 asks that the affected occurrence's state be
// "unambiguous", and FR-5.x asks for collisions to be "surfaced to the user at
// creation, not silently dropped" — this is that, for the blackout direction.
//
// **The one type in this feature shared by two endpoints**, and deliberately so.
// Decision 0015's per-endpoint rule is about a *response*: the shape one endpoint
// promises, which no other endpoint should be able to freeze. This is not that —
// it is BlackoutCascade's output, embedded by both the create and the edit
// responses because both run the same cascade and have to report the same fact.
// Same footing 0015 gives IssuedTokens (TokenIssuer's output) and Phase 3 gave
// ApproverSummary (IUserRepository's output): a component's return type, not a
// contract two endpoints are borrowing from each other.
//
// RecurrenceRuleId is here so an admin can see they have just punched a hole in
// a series rather than cancelled a one-off; null means a standalone booking. The
// series itself is untouched, per decision 0001 — only the occurrences.
//
// UserId rather than a name or an email: a TenantAdmin may cancel any booking in
// their tenant (decision 0002) so the owner is theirs to know, but the id is
// enough to look one up and an address is more than this endpoint was asked for
// — the same line GET /resources/{id} draws for approvers.
public sealed record CancelledBookingSummary(
    Guid BookingId,
    Guid UserId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    Guid? RecurrenceRuleId);
