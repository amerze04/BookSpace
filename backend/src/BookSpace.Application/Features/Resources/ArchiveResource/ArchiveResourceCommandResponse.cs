namespace BookSpace.Application.Features.Resources.ArchiveResource;

// The 200 body of POST /resources/{id}/archive. FR-3.5: archiving preserves the
// resource, so the useful reply is the row with IsArchived flipped rather than
// a 204.
//
// Its own type, not the read detail's (convention agreed 2026-09-01). This is
// the response most likely to diverge: an archived resource has no future
// availability worth reporting, so if the read detail grows availability windows
// in Phase 3, this one should probably not follow.
public sealed record ArchiveResourceCommandResponse(
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
