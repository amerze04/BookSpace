using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ArchiveResource;

// FR-3.5: resources are archived, never deleted — archiving preserves the
// resource and its booking history while taking it out of circulation.
//
// POST /resources/{id}/archive rather than DELETE /resources/{id}: see the
// controller for the trade-off. Returns the archived resource's own
// representation, so a client sees isArchived flip rather than having to re-read.
public sealed record ArchiveResourceCommand(Guid ResourceId) : IRequest<ResourceDetailResponse>;
