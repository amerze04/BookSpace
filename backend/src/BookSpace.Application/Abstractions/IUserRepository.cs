using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// Reads over Users for callers that are inside a tenant context — as opposed to
// IAuthenticationUserRepository, which is the one sanctioned IgnoreQueryFilters()
// path because login runs before a tenant exists (CLAUDE.md §4.2).
//
// The contrast is the point: nothing here bypasses anything, so a user id from
// another tenant simply does not come back, and the caller cannot tell it apart
// from an id that exists nowhere. That is what makes ApproverNotEligible safe to
// return for both cases (docs/decisions/0016).
public interface IUserRepository
{
    // The subset of candidateUserIds that may be assigned as approvers of a
    // resource in the current tenant (FR-3.3). Ineligible ids are simply absent;
    // the caller reports the difference, and deliberately cannot say which of the
    // three reasons applied.
    //
    // Eligible means all of:
    //   - in the caller's own tenant — enforced by the query filter and RLS, not
    //     by a predicate written here
    //   - active (a deactivated approver would silently stall every approval)
    //   - holding Approver or TenantAdmin
    //
    // Approver *or* TenantAdmin, matching AuthorizationPolicies.Approver: the set
    // that may be assigned and the set that may actually approve have to be the
    // same one, or a TenantAdmin could be refused assignment to a resource they
    // are nonetheless entitled to approve. Owner's call, 2026-09-01.
    Task<IReadOnlyCollection<Guid>> FindEligibleApproverIdsAsync(
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken cancellationToken);

    // Names for an approver list, ordered by full name so a client renders them
    // without sorting. Tenant-filtered like everything else here, so an id that
    // has since left the tenant drops out rather than surfacing as a blank row.
    Task<IReadOnlyList<ApproverSummary>> FindApproverSummariesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken);

    // GET /users, paged, for both of its callers. `query.Scope` decides which:
    // omitted gives the decision `0018` eligible-approver set (the approvers
    // picker, admin console phase 1), `All` gives every user in the tenant
    // (the directory, user management phase 4).
    //
    // Was ListEligibleApproversAsync until phase 4, and renamed rather than
    // joined by a second method: two methods would be two places the paging,
    // searching and ordering are written, and the one nobody looks at is the one
    // that drifts. The eligibility *predicate* is still stated once and shared
    // with FindEligibleApproverIdsAsync above — if those two disagreed, an admin
    // would be offered somebody the write path then refuses, with
    // ApproverNotEligible and no explanation available.
    //
    // Neither scope reaches past the tenant: that is the query filter's and RLS's
    // job, not a parameter's.
    Task<PagedResult<ListUsersQueryResponse>> ListAsync(
        ListUsersQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken);

    // The current tenant's active TenantAdmins (decision 0028).
    //
    // **Who is told about an approval request on a gated resource that has no
    // approvers assigned.** Since 0028 removed FR-3.3's implies-approvers
    // invariant, that state is legal and expected — it is what a resource looks
    // like between being created gated and having its approver list filled in.
    // `ApprovalRequested` used to be built one-per-approver on the assumption
    // that the list could never be empty; without this it would now be built
    // for nobody, and FR-9.3's stale-approval job would quietly expire requests
    // no human was ever told about.
    //
    // TenantAdmin only, not the wider eligible-approver set: this mirrors
    // `BookingApprovalReach`, where a TenantAdmin reaches any resource and an
    // Approver reaches only the ones listing them. Notifying an Approver about a
    // resource they cannot act on would be worse than notifying nobody.
    Task<IReadOnlyCollection<Guid>> FindTenantAdminUserIdsAsync(CancellationToken cancellationToken);

    // User management phase 3: POST /users.
    void Add(User user);

    // Persists everything tracked on this unit of work — the new user, its role
    // assignment, and the activation token added alongside it through
    // IActivationTokenRepository, which shares the same DbContext. One save, so
    // a provisioned account and the only means of signing into it cannot exist
    // without each other.
    //
    // **Throws EmailAlreadyInUseException when UQ_Users_Email refuses the
    // insert**, which is the only place that refusal is decided. There is no
    // pre-check, deliberately: a read that could see another tenant's row would
    // have to be an unfiltered one, and CLAUDE.md §4.2 keeps that surface to the
    // two named authentication methods. Letting the unique index answer is both
    // race-free (CLAUDE.md §6 puts uniqueness in tier 1) and structurally
    // incapable of telling the caller which tenant the collision is in.
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
