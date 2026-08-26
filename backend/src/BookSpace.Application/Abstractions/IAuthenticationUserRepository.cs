using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Abstractions;

// The authentication-bootstrap exception CLAUDE.md §4.2 allows: login runs
// unauthenticated, so there is no tenant context yet and the global query
// filter Phase 4 adds to Users would match nothing. These two methods are the
// explicitly-named IgnoreQueryFilters() surface — nothing else in the codebase
// may read Users unfiltered.
//
// Returns the owning organization's status alongside the user so FR-2.4 can be
// enforced without a second round trip. Status is null for a SysAdmin, who has
// no OrgId.
public sealed record AuthenticatedUser(User User, OrganizationStatus? OrganizationStatus);

public interface IAuthenticationUserRepository
{
    Task<AuthenticatedUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<AuthenticatedUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);
}
