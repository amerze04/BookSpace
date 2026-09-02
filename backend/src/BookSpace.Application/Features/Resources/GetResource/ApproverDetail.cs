namespace BookSpace.Application.Features.Resources.GetResource;

// One assigned approver on the read detail (FR-3.3), ordered by full name.
//
// Its own type rather than ReplaceApprovers' AssignedApprover (convention agreed
// 2026-09-01, docs/decisions/0015). Identical today; this is the one every member
// of the tenant sees, so it is the one that must stay conservative about what it
// carries — the write response is free to grow admin-only detail.
//
// FullName and no email: a bare Guid tells a member nothing about who approves
// the room they are booking, and an address tells them more than they asked for.
public sealed record ApproverDetail(Guid UserId, string FullName);
