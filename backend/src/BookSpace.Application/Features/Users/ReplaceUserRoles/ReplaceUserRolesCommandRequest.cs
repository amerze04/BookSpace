using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.ReplaceUserRoles;

// PUT /users/{id}/roles. TenantAdmin only. FR-1.5 ("a user belongs to one tenant
// and may hold multiple roles within it") and PRD §2's persona line.
//
// **Replace-the-set, not add/remove** — the phase-5 call the plan left open,
// and the same answer PUT /resources/{id}/approvers gives, for the same reason:
// per-role POST/DELETE would make an admin swapping Approver for TenantAdmin
// pass through an intermediate state with either both or neither, and which one
// depends on the order the client happened to pick. A whole new set in one
// request has no intermediate state at all.
//
// It also makes the last-admin guard statable. Over a final set the question is
// "does this set still contain TenantAdmin?"; over a sequence of deltas it would
// have to be re-asked after each one, and the answer in between is not a state
// the tenant should ever be in.
//
// admin-plan.md §4.1's lesson is that the API's shape should decide the
// screen's, so phase 7 renders a checkbox group saved in one go — not per-role
// toggles.
//
// Bare roles, no ids: the user is in the path. SysAdmin is refused by the
// validator; see there for why that matters more than it looks.
public sealed record ReplaceUserRolesCommandRequest(Guid UserId, IReadOnlyList<Role> Roles)
    : IRequest<ReplaceUserRolesCommandResponse>;
