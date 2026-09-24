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

    // The write half of the same exemption, for account activation — the first
    // operation that changes a Users row from an unauthenticated request.
    //
    // The scope is not optional here, and the failure without it is quiet
    // rather than loud: RLS is a *filter* predicate (see the AddTenantIsolationRls
    // migration), and a filter predicate applies to the rows an UPDATE can see.
    // With no tenant context the Users row is invisible, the UPDATE matches
    // nothing, and EF reports a DbUpdateConcurrencyException about a row that is
    // plainly there — which reads as a race, not as a missing bypass.
    //
    // It also stands the SaveChanges ownership guard down (CLAUDE.md §4.2
    // mechanism 2, BookSpaceDbContext.ValidateTenantOwnership), because the two
    // mechanisms have to agree about what a bypass means. Before this there was
    // no writer inside a bypass scope with a tenant context present, so they
    // never had to.
    // async, and awaiting inside the scope, deliberately: returning the Task
    // without awaiting would dispose the scope the instant this method returns,
    // while the save was still in flight and had not yet opened the connection
    // the interceptor reads the flag from.
    public async Task SaveChangesUnfilteredAsync(CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        await _context.SaveChangesAsync(cancellationToken);
    }

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
