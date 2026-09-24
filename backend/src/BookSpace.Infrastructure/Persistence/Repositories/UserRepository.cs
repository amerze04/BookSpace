using System.Linq.Expressions;
using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Data.SqlClient;
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

    // User management phase 3. Tracked through the tenant-filtered DbSet like
    // everything else here — BookSpaceDbContext's SaveChanges guard (§4.2
    // mechanism 2) then refuses the insert outright if the new user's OrgId
    // disagrees with the caller's tenant, so nothing in the handler has to
    // re-check it.
    public void Add(User user) => _context.Users.Add(user);

    // Also persists the ActivationToken the create handler added through
    // IActivationTokenRepository: both repositories hold this same scoped
    // DbContext, so one SaveChanges covers the account, its role and the only
    // means of signing into it.
    //
    // **The one place a duplicate email is refused.** There is deliberately no
    // pre-check in the handler: seeing another tenant's row would require an
    // unfiltered read, and CLAUDE.md §4.2 keeps that surface to the two named
    // methods on IAuthenticationUserRepository. Letting UQ_Users_Email answer is
    // race-free (§6 puts uniqueness in tier 1) and means nothing on this path
    // can learn which tenant the collision is in — which is what makes
    // EmailAlreadyInUse safe to return for both cases (decision `0010`,
    // docs/user-management-plan.md §3.2).
    //
    // 2601 is a duplicate key on a unique *index*, which is what
    // HasIndex(...).IsUnique() creates and therefore what actually fires here;
    // 2627 is the PRIMARY KEY / named UNIQUE CONSTRAINT form, included because
    // nothing stops a future migration changing the enforcement mechanism. The
    // same pair RecurrenceRuleRepository already tests for.
    //
    // The index name is checked, not just the error number: Users also carries
    // UQ_Users_CalendarFeedToken and UQ_Users_Org_Id, and reporting either of
    // those as "that email is taken" would send an admin looking for a problem
    // that is not there. Anything else rethrows.
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql
                && sql.Message.Contains(EmailUniqueIndexName, StringComparison.Ordinal))
        {
            throw new EmailAlreadyInUseException();
        }
    }

    // As named in UserConfiguration. A constant rather than a literal in the
    // filter above, because a renamed index that nobody noticed here would turn
    // a 409 into a 500 and only in production data.
    private const string EmailUniqueIndexName = "UQ_Users_Email";

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

    // Decision 0028's fallback recipients. The same shape as the predicate
    // above and for the same reason — the role test has to run in SQL, not in
    // memory — but narrowed to TenantAdmin alone, matching who
    // BookingApprovalReach says can decide on any resource in the tenant.
    public async Task<IReadOnlyCollection<Guid>> FindTenantAdminUserIdsAsync(
        CancellationToken cancellationToken)
    {
        return await _context.Users
            .AsNoTracking()
            .Where(u => u.IsActive
                && EF.Property<ICollection<User.RoleAssignment>>(u, RoleAssignmentsNavigation)
                    .Any(r => r.Role == Role.TenantAdmin))
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

    // GET /users, for both callers. User management phase 4 added the "all
    // users" branch this method's own comment used to say did not exist — see
    // UserScope for why it is opt-in rather than the default.
    //
    // The two scopes differ by exactly one `Where`, and that is deliberate:
    // paging, searching and ordering are written once, so the directory and the
    // picker cannot come to disagree about what page 2 contains.
    public Task<PagedResult<ListUsersQueryResponse>> ListAsync(
        ListUsersQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken)
    {
        var users = _context.Users.AsNoTracking();

        // Not a ternary inside the Where: an unrecognized scope must not
        // silently pick a branch. UserScope has two values and the validator has
        // already refused anything else, so the default arm is unreachable —
        // which is exactly why it throws rather than guessing.
        users = query.Scope switch
        {
            UserScope.EligibleApprovers => users.Where(IsEligibleApprover),
            UserScope.All => users,
            _ => throw new ArgumentOutOfRangeException(
                nameof(query),
                query.Scope,
                "Unsupported user scope."),
        };

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
                u.IsActive,
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
