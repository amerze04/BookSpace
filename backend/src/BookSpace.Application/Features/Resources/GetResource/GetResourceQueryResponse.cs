using BookSpace.Domain.Enums;

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
// Approvers is on the read detail and not on the PUT payload either, for the
// same reason as the windows: assignment has its own endpoint
// (PUT /resources/{id}/approvers), and a resource edit that omitted the array
// would otherwise clear the list — which FR-3.3 forbids outright whenever
// RequiresApproval is set.
//
// Visible to any TenantMember, not just an admin: a member deciding whether to
// book a room that needs approval should be able to see who will be deciding.
// Names only, no email addresses — see ApproverDetail.
public sealed record GetResourceQueryResponse(
    Guid Id,
    string Name,
    string? Description,
    ResourceType ResourceType,
    int Capacity,
    string TimeZoneId,
    bool RequiresApproval,
    int? MinDurationMinutes,
    int? MaxDurationMinutes,
    bool IsArchived,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<AvailabilityWindowDetail> AvailabilityWindows,
    IReadOnlyList<ApproverDetail> Approvers);
