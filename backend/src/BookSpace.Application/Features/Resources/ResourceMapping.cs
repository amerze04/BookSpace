using BookSpace.Application.Features.Resources.ArchiveResource;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.UpdateResource;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Resources;

// Hand-written, per docs/decisions/0015 (no AutoMapper). Used by the write
// handlers, which have the entity in hand after saving and would otherwise have
// to re-read it through the repository just to shape a response.
//
// One method per response type, because each endpoint owns its own response
// (convention agreed 2026-09-01). The three bodies are near-identical today,
// which is the visible cost of that convention — and the right place for it to
// be visible, since this is where a field added to one response but not another
// shows up as an obvious difference rather than as silent coupling.
//
// The read paths do NOT come through here: ResourceRepository projects straight
// to GetResourceQueryResponse and ListResourcesQueryResponse inside the SQL
// query, so it never materializes an entity to map. All the response records are
// positional, so a field added to one is a compile error at every construction
// site rather than a default value nobody noticed.
internal static class ResourceMapping
{
    public static CreateResourceCommandResponse ToCreateResponse(this Resource resource) =>
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

    public static ArchiveResourceCommandResponse ToArchiveResponse(this Resource resource) =>
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

    public static UpdateResourceCommandResponse ToUpdateResponse(
        this Resource resource,
        TimeZoneChangeNotice? timeZoneChange) =>
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
            resource.UpdatedAtUtc,
            timeZoneChange);
}
