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

    // The write half of the same bootstrap exemption, added for account
    // activation — the first operation in this system that *changes* a user from
    // an unauthenticated request.
    //
    // It exists because the two reads above are not enough on their own. A
    // Users row belonging to a real tenant is invisible to row-level security
    // when there is no tenant context, and an UPDATE against an invisible row
    // affects zero rows — so a plain SaveChangesAsync here would not fail
    // loudly, it would surface as a DbUpdateConcurrencyException about a row
    // that is sitting right there. This saves inside the same explicitly-named
    // bypass the reads use.
    //
    // Named for what is unusual about it rather than for what it does. Nothing
    // outside the authentication feature may call it, for exactly the reason
    // CLAUDE.md §4.2 restricts IgnoreQueryFilters(): it is the one write path
    // in the application that is not tenant-scoped, and it is safe only because
    // the caller reached the user through a single-use secret rather than
    // through an id a client chose.
    Task SaveChangesUnfilteredAsync(CancellationToken cancellationToken);
}
