using Microsoft.AspNetCore.Authorization;

namespace BookSpace.Api.Authorization;

// Marker requirement for AuthorizationPolicies.ActiveTenantAdminWrite. No data
// of its own — see ActiveTenantAdminAuthorizationHandler for what it checks and
// why the ordinary role-claim check (AuthorizationPolicies.TenantAdmin) is not
// enough on its own for the handful of routes that need this.
public sealed class ActiveTenantAdminRequirement : IAuthorizationRequirement
{
}
