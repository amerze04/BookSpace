using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Messaging;

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
// refused here for now — see ResourceWriteRules.EnsureApproversWhenRequired.
public sealed record CreateResourceCommand(
    string Name,
    string? Description,
    string ResourceType,
    int Capacity,
    string TimeZoneId,
    bool RequiresApproval,
    int? MinDurationMinutes,
    int? MaxDurationMinutes) : IRequest<ResourceDetailResponse>, IResourceWriteCommand;
