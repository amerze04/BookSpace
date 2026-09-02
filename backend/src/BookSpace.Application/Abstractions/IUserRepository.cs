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
}
