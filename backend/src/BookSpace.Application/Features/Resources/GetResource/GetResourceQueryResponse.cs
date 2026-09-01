namespace BookSpace.Application.Features.Resources.GetResource;

// GET /resources/{id}. The full editable representation of a resource, which is
// also what PUT /resources/{id} takes — an edit is a full representation
// (docs/decisions/0015), so the two shapes matching is the point.
//
// CreatedByUserId/UpdatedByUserId are deliberately NOT here: the timestamps
// answer "when did this last change", which is useful to an admin, while the
// user ids are bare Guids a client cannot resolve to a person and would only
// leak who administers the tenant.
//
// AvailabilityWindows is on the read detail but NOT on the PUT payload, which is
// the one place this response and the edit command deliberately diverge. The
// schedule is replaced through its own endpoint (FR-3.2,
// PUT /resources/{id}/availability-windows), so folding it into the resource
// edit would give two ways to write the same rows — and the resource edit is a
// full representation, meaning an admin renaming a room while omitting the
// windows array would silently wipe the schedule.
//
// One thing a reader might still expect and will not find: the assigned approver
// list (FR-3.3), which lands with the approver endpoint later in Phase 3.
// RequiresApproval can therefore read true here with no visible approvers; that
// gap closes then, and adding the field is additive.
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
    DateTime UpdatedAtUtc,
    IReadOnlyList<AvailabilityWindowDetail> AvailabilityWindows);
