using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.UpdateResource;

// FR-3.1 / FR-3.5, TenantAdmin only. A full representation, not a patch
// (docs/decisions/0015): every mutable field is supplied, and an omitted
// nullable field means cleared rather than unchanged. That is why the payload
// is identical to the create command's plus the id — and why the fields match
// GetResourceQueryResponse, which is what a client would have read first.
//
// ResourceId comes from the route, not the body, so the two cannot disagree.
public sealed record UpdateResourceCommandRequest(
    Guid ResourceId,
    string Name,
    string? Description,
    string ResourceType,
    int Capacity,
    string TimeZoneId,
    bool RequiresApproval,
    int? MinDurationMinutes,
    int? MaxDurationMinutes) : IRequest<UpdateResourceCommandResponse>, IResourceWriteCommand;
