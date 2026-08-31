using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.GetResource;

// FR-3.1. The 404 path is also the cross-tenant path: the global query filter
// and RLS (CLAUDE.md §4.2) make another tenant's real id return null here, and
// ResourceNotFound is the only thing this may say about it — confirming that an
// id exists somewhere is exactly the leak AC-4 forbids.
public sealed class GetResourceQueryHandler : IRequestHandler<GetResourceQuery, ResourceDetailResponse>
{
    private readonly IResourceRepository _resources;

    public GetResourceQueryHandler(IResourceRepository resources)
    {
        _resources = resources;
    }

    public async Task<ResourceDetailResponse> Handle(
        GetResourceQuery request,
        CancellationToken cancellationToken)
    {
        var resource = await _resources.FindDetailAsync(request.ResourceId, cancellationToken);

        // The message is for the log only (see AppException) — the response
        // carries the reason code and nothing else.
        return resource ?? throw new AppException(
            ErrorKind.NotFound,
            ReasonCodes.ResourceNotFound,
            $"Resource {request.ResourceId} was not found in the current tenant.");
    }
}
