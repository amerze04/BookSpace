using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.GetResource;

// FR-3.1. An archived resource is still readable by id (FR-3.5) — only the
// list hides it by default.
public sealed record GetResourceQuery(Guid ResourceId) : IRequest<ResourceDetailResponse>;
