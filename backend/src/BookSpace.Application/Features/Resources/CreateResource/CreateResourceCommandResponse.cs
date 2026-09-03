using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Resources.CreateResource;

// The 201 body of POST /resources.
//
// Field-for-field identical to GetResourceQueryResponse today, and deliberately
// a separate type anyway (convention agreed 2026-09-01): a response DTO is one
// endpoint's contract, not a shape several endpoints borrow. Phase 3 is the
// concrete reason — availability windows and the approver list belong on the
// read detail, and a shared type would put them in this 201 body too, where a
// client has no use for a window list it has not created yet.
//
// The duplication is the cost, and it is visible on purpose: four near-identical
// records beat one shape that four endpoints cannot change independently.
public sealed record CreateResourceCommandResponse(
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
    DateTime UpdatedAtUtc);
