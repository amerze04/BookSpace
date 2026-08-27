using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// CLAUDE.md §4.2 permits IgnoreQueryFilters() only in explicitly named methods.
// These are those methods, and this is the reason: authentication runs before a
// tenant context exists, so once Phase 4 puts a global query filter on Users the
// filter would match nothing and every login would fail. Nothing else in the
// codebase may read Users unfiltered.
//
// IgnoreQueryFilters() only skips the ORM-generated WHERE clause — it has no
// effect on SQL Server row-level security, which the engine enforces
// independently via SESSION_CONTEXT. QueryUnfiltered also enters
// TenantBypassScope so the RLS layer is told the same thing the EF layer
// already knows: this query is deliberately scopeless, not forgotten.
//
// FirstOrDefaultAsync, never DbSet.Find() — Find can return a tracked entity
// without querying, which would bypass the filter for the wrong reason.
internal sealed class AuthenticationUserRepository : IAuthenticationUserRepository
{
    private readonly BookSpaceDbContext _context;

    public AuthenticationUserRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public Task<AuthenticatedUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        QueryUnfiltered(u => u.Email == normalizedEmail, cancellationToken);

    public Task<AuthenticatedUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        QueryUnfiltered(u => u.Id == userId, cancellationToken);

    // Projects the owning organization's status in the same query so FR-2.4 costs
    // no extra round trip. Organizations is not tenant-filtered, but it is joined
    // here rather than loaded separately for the same reason.
    private async Task<AuthenticatedUser?> QueryUnfiltered(
        System.Linq.Expressions.Expression<Func<User, bool>> predicate,
        CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        var row = await _context.Users
            .IgnoreQueryFilters()
            .Where(predicate)
            .Select(u => new
            {
                User = u,
                OrganizationStatus = _context.Organizations
                    .Where(o => o.Id == u.OrgId)
                    .Select(o => (OrganizationStatus?)o.Status)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : new AuthenticatedUser(row.User, row.OrganizationStatus);
    }
}
