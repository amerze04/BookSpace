using System.Linq.Expressions;
using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Domain.Entities;
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
    // User.Roles is a computed property over the owned _roleAssignments
    // collection, so it has no SQL translation; the navigation has to be named
    // to be queried. The string is the same one UserConfiguration already passes
    // to OwnsMany and Navigation(), so this is not a second, looser way of
    // naming the same thing — it is the one name, used twice.
    //
    // **This was a deliberate reversal, admin console phase 1.** The eligibility
    // rule used to run in memory (see FindEligibleApproverIdsAsync below), on the
    // argument that an EF.Property expression over a private backing field is
    // fragile and unreadable, and that the candidate set was one approver list —
    // a handful of rows. That trade stops working the moment the same rule has to
    // produce a *paged* answer: filtering after OFFSET/FETCH would page over the
    // wrong set and report a TotalCount that counts ineligible people. Correct
    // paging needs the predicate in SQL, so it is in SQL, once, here.
    private const string RoleAssignmentsNavigation = "_roleAssignments";

    // The one definition of "eligible" (decision `0018`): own-tenant — which is
    // the DbSet's job, not this predicate's — active, and holding Approver or
    // TenantAdmin.
    //
    // Approver *or* TenantAdmin matches AuthorizationPolicies.Approver: the set
    // that may be assigned and the set that may actually approve have to be the
    // same one (owner's call, 2026-09-01).
    //
    // Both public methods below go through this. Two copies of it would be two
    // things that can disagree, and the way they would disagree is the worst
    // one available: an admin offered somebody the write path then refuses, with
    // `0018` collapsing every reason into ApproverNotEligible so the screen
    // cannot say why.
    private static readonly Expression<Func<User, bool>> IsEligibleApprover =
        u => u.IsActive
            && EF.Property<ICollection<User.RoleAssignment>>(u, RoleAssignmentsNavigation)
                .Any(r => r.Role == Role.Approver || r.Role == Role.TenantAdmin);

    private readonly BookSpaceDbContext _context;

    public UserRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<Guid>> FindEligibleApproverIdsAsync(
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken cancellationToken)
    {
        if (candidateUserIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        return await _context.Users
            .AsNoTracking()
            .Where(u => candidateUserIds.Contains(u.Id))
            .Where(IsEligibleApprover)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
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

    // GET /users. Note what this method does *not* do: there is no "all users"
    // branch and no parameter that would produce one. The route is narrower than
    // its name on purpose — see ListUsersQueryRequest for why.
    public Task<PagedResult<ListUsersQueryResponse>> ListEligibleApproversAsync(
        ListUsersQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken)
    {
        var users = _context.Users
            .AsNoTracking()
            .Where(IsEligibleApprover);

        // FullName or Email, the two things the picker actually shows. Whitespace
        // -only counts as "no search"; EF translates Contains to a LIKE, whose
        // case sensitivity follows the database's own collation rather than
        // anything decided here — the same non-decision GET /resources' search
        // makes.
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            users = users.Where(u => u.FullName.Contains(search) || u.Email.Contains(search));
        }

        // The projection happens after ordering so ToPagedResultAsync still sees
        // the OrderBy in the expression tree, and so COUNT(*) runs over the
        // filtered set rather than a materialized list.
        //
        // Roles come back as a collection projection over the same owned
        // navigation the predicate filters on. EF issues it as a second query and
        // stitches the rows together; it is the child table this endpoint's
        // filter is already reading, not a new join.
        return ApplyOrder(users, sort)
            .Select(u => new ListUsersQueryResponse(
                u.Id,
                u.FullName,
                u.Email,
                EF.Property<ICollection<User.RoleAssignment>>(u, RoleAssignmentsNavigation)
                    .Select(r => r.Role)
                    .ToList()))
            .ToPagedResultAsync(query, cancellationToken);
    }

    // Maps the canonical sort field onto a typed OrderBy — a `sort` value never
    // reaches a LINQ expression as a string. Every branch ends in ThenBy(Id):
    // offset paging over a non-unique order can silently overlap or skip rows
    // between pages, and two people can share a name.
    private static IOrderedQueryable<User> ApplyOrder(IQueryable<User> users, SortOption? sort)
    {
        // Full name ascending by default, because that is the order a picker
        // reads in and the order FindApproverSummariesAsync already returns the
        // assigned list in — the two halves of the same screen agreeing.
        if (sort is null)
        {
            return users.OrderBy(u => u.FullName).ThenBy(u => u.Id);
        }

        return sort.Field switch
        {
            UserSortFields.Email => sort.Descending
                ? users.OrderByDescending(u => u.Email).ThenBy(u => u.Id)
                : users.OrderBy(u => u.Email).ThenBy(u => u.Id),
            _ => sort.Descending
                ? users.OrderByDescending(u => u.FullName).ThenBy(u => u.Id)
                : users.OrderBy(u => u.FullName).ThenBy(u => u.Id),
        };
    }
}
