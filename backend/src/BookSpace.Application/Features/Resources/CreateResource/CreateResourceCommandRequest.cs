using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Resources.CreateResource;

// FR-3.1. TenantAdmin only (see ResourcesController) — WP-3's AC "non-admins
// cannot create or edit resources".
//
// No OrgId, no CreatedByUserId: both come from the token, via ICurrentTenant
// and ICurrentUser. Accepting either on the wire would make forging one a
// matter of editing a request body.
//
// No approver list either: FR-3.3's approver assignment is Phase 3, where the
// eligibility rules to validate it live. RequiresApproval = true is therefore
// accepted here since decision 0028 — the resource is gated from creation and
// its requests fall to the tenant's admins until approvers are assigned.
public sealed record CreateResourceCommandRequest(
    string Name,
    string? Description,
    ResourceType ResourceType,
    int Capacity,
    string TimeZoneId,
    bool RequiresApproval,
    int? MinDurationMinutes,
    int? MaxDurationMinutes) : IRequest<CreateResourceCommandResponse>, IResourceWriteCommand;
