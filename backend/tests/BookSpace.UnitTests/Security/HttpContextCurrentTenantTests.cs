using System.Security.Claims;
using BookSpace.Api.Tenancy;
using BookSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace BookSpace.UnitTests.Security;

// CLAUDE.md §4.2: every branch resolves to null rather than throwing — a null
// ICurrentTenant.OrgId is a meaningful, expected state (SysAdmin, or no
// request in flight), not an error.
public class HttpContextCurrentTenantTests
{
    [Fact]
    public void OrgId_ClaimPresent_ReturnsParsedGuid()
    {
        var orgId = Guid.NewGuid();
        var currentTenant = CreateWithClaims(new Claim(BookSpaceClaims.OrgId, orgId.ToString()));

        Assert.Equal(orgId, currentTenant.OrgId);
    }

    [Fact]
    public void OrgId_ClaimAbsent_ReturnsNull()
    {
        var currentTenant = CreateWithClaims();

        Assert.Null(currentTenant.OrgId);
    }

    [Fact]
    public void OrgId_NoHttpContext_ReturnsNull()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Null(currentTenant.OrgId);
    }

    private static HttpContextCurrentTenant CreateWithClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return new HttpContextCurrentTenant(accessor);
    }
}
