using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Security;

namespace BookSpace.Api.Tenancy;

// Reads the orgId claim JwtAccessTokenService mints (docs/decisions/0009).
// Every branch below — no HttpContext, no principal, an unauthenticated
// request (e.g. the login request itself), or a SysAdmin with no orgId claim
// — resolves to null rather than throwing, since ICurrentTenant.OrgId being
// null is a meaningful, expected state (see ICurrentTenant), not an error.
internal sealed class HttpContextCurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentTenant(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? OrgId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User?.FindFirst(BookSpaceClaims.OrgId)?.Value;
            return Guid.TryParse(value, out var orgId) ? orgId : null;
        }
    }
}
