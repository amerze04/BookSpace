using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Resources;

// Hand-written, per docs/decisions/0015 (no AutoMapper). Used by the write
// handlers, which have the entity in hand after saving and would otherwise have
// to re-read it through the repository just to shape a response.
//
// The read paths do NOT use this: ResourceRepository projects straight to the
// DTO inside the SQL query, so it never materializes an entity to map. The two
// therefore have to produce the same shape independently — which is why
// ResourceDetailResponse is a positional record, so a field added there breaks
// both sides at compile time rather than silently appearing in one.
internal static class ResourceMapping
{
    public static ResourceDetailResponse ToDetailResponse(this Resource resource) =>
        new(
            resource.Id,
            resource.Name,
            resource.Description,
            resource.ResourceType,
            resource.Capacity,
            resource.TimeZoneId,
            resource.RequiresApproval,
            resource.MinDurationMinutes,
            resource.MaxDurationMinutes,
            resource.IsArchived,
            resource.CreatedAtUtc,
            resource.UpdatedAtUtc);
}
