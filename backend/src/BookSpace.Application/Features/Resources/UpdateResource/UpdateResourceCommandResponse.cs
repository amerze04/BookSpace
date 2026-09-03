using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Resources.UpdateResource;

// The 200 body of PUT /resources/{id}: the updated resource's fields, plus the
// one thing an edit can do that those fields do not show.
//
// Flat rather than wrapping a nested resource object. The old shape wrapped the
// shared read-detail DTO, which is exactly the sharing the 2026-09-01 convention
// removes — and once this endpoint owns its own response type, a wrapper buys
// nothing but a level of nesting. So `{ …fields, timeZoneChange }` rather than
// `{ resource: { …fields }, timeZoneChange }`. This is the only observable wire
// change from that refactor.
//
// wp3-plan's "smaller calls": changing TimeZoneId **reinterprets** every existing
// availability window rather than shifting it, because a window is stored as
// resource-local wall-clock time (docs/decisions/0003) — 09:00–17:00 stays
// 09:00–17:00 and starts meaning a different instant. That is defensible but
// surprising, so the response says it happened instead of leaving the admin to
// discover it from a booking that lands an hour off. Null whenever the timezone
// did not change, which is the normal case.
public sealed record UpdateResourceCommandResponse(
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
    TimeZoneChangeNotice? TimeZoneChange);
