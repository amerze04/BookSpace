using BookSpace.Application.Abstractions;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// FR-3.3 approver eligibility. The deliberate opposite of
// AuthenticationUserRepository: no IgnoreQueryFilters, no TenantBypassScope,
// nowhere in this file. Every query goes through the tenant-filtered DbSet, so
// CLAUDE.md §4.2's query filter and RLS are what make another tenant's user
// invisible — not a WHERE clause written here that someone could forget.
//
// That is also what makes ApproverNotEligible honest: a cross-tenant id does not
// come back from these queries at all, so the handler cannot report it as
// anything more specific even if it wanted to.
internal sealed class UserRepository : IUserRepository
{
    private readonly BookSpaceDbContext _context;

    public UserRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // The role test runs in memory, not in SQL, and that is a deliberate trade.
    // User.Roles is a computed property over the owned _roleAssignments
    // collection, so it has no translation; pushing it into the query would mean
    // an EF.Property expression over a private backing field, which is both
    // fragile and unreadable. Loading the candidates first costs one query over
    // a set the size of one approver list — a handful of rows — and lets the rule
    // be stated in the domain's own vocabulary. AuthenticationUserRepository
    // makes the same call for the same reason.
    //
    // IsActive *is* in the query: it is a plain column, and filtering it in SQL
    // keeps a deactivated user out of memory rather than merely out of the answer.
    public async Task<IReadOnlyCollection<Guid>> FindEligibleApproverIdsAsync(
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken cancellationToken)
    {
        if (candidateUserIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var candidates = await _context.Users
            .AsNoTracking()
            .Where(u => candidateUserIds.Contains(u.Id) && u.IsActive)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(u => u.Roles.Contains(Role.Approver) || u.Roles.Contains(Role.TenantAdmin))
            .Select(u => u.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<ApproverSummary>> FindApproverSummariesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return Array.Empty<ApproverSummary>();
        }

        return await _context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .OrderBy(u => u.FullName)
            .ThenBy(u => u.Id)
            .Select(u => new ApproverSummary(u.Id, u.FullName))
            .ToListAsync(cancellationToken);
    }
}
