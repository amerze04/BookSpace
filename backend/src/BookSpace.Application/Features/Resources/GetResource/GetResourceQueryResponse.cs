namespace BookSpace.Application.Features.Resources.GetResource;

// GET /resources/{id}. The full editable representation of a resource, which is
// also what PUT /resources/{id} takes in Phase 2 step 3 — an edit is a full
// representation (docs/decisions/0015), so the two shapes matching is the point.
//
// CreatedByUserId/UpdatedByUserId are deliberately NOT here: the timestamps
// answer "when did this last change", which is useful to an admin, while the
// user ids are bare Guids a client cannot resolve to a person and would only
// leak who administers the tenant.
//
// Two things a reader might expect and will not find until later phases:
// availability windows (Phase 3, FR-3.2) and the assigned approver list
// (Phase 3, FR-3.3, where there are endpoints to manage it and eligibility
// rules to report). RequiresApproval can therefore read true here with no
// visible approvers — that gap closes in Phase 3, and adding either field later
// is additive.
public sealed record GetResourceQueryResponse(
    Guid Id,
    string Name,
    string? Description,
    string ResourceType,
    int Capacity,
    string TimeZoneId,
    bool RequiresApproval,
    int? MinDurationMinutes,
    int? MaxDurationMinutes,
    bool IsArchived,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
