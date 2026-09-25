using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ReactivateUser;

// POST /users/{id}/reactivate. TenantAdmin only. The other half of FR-2.4's
// suspension: somebody who came back, or who was deactivated by mistake.
//
// **No last-admin guard here, and that is not an omission.** The invariant is
// that a tenant keeps at least one active TenantAdmin; reactivating can only
// ever grow that set, never empty it.
//
// Unlike archiving a resource — which FR-3.5 makes deliberately irreversible —
// deactivation is a reversible state, so this endpoint exists where an
// "unarchive" pointedly does not.
public sealed record ReactivateUserCommandRequest(Guid UserId)
    : IRequest<ReactivateUserCommandResponse>;
