using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace BookSpace.UnitTests.Security;

// Locks in the claim shape from
// docs/decisions/0009-jwt-claims-and-token-lifetimes.md. Phase 4's tenant
// accessor reads orgId back out of these tokens, so the shape is a contract,
// not an implementation detail.
public class JwtAccessTokenServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
    private const string SigningKey = "test-signing-key-that-is-long-enough-32";

    [Fact]
    public void Issue_IncludesSubjectEmailAndJti()
    {
        var user = CreateUser(orgId: Guid.NewGuid(), Role.Member);

        var token = ReadToken(Issue(user));

        Assert.Equal(user.Id.ToString(), token.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal(user.Email, token.Claims.Single(c => c.Type == "email").Value);
        Assert.NotEmpty(token.Claims.Single(c => c.Type == "jti").Value);
    }

    [Fact]
    public void Issue_TenantUser_IncludesOrgIdClaim()
    {
        var orgId = Guid.NewGuid();
        var user = CreateUser(orgId, Role.Member);

        var token = ReadToken(Issue(user));

        Assert.Equal(orgId.ToString(), token.Claims.Single(c => c.Type == BookSpaceClaims.OrgId).Value);
    }

    // The decision that matters most here: absent, not empty. An empty or
    // Guid.Empty orgId could be mistaken for a real tenant by the Phase 4
    // accessor; a missing claim cannot.
    [Fact]
    public void Issue_SysAdmin_OmitsOrgIdClaimEntirely()
    {
        var user = CreateUser(orgId: null, Role.SysAdmin);

        var token = ReadToken(Issue(user));

        Assert.DoesNotContain(token.Claims, c => c.Type == BookSpaceClaims.OrgId);
    }

    // FR-1.5: roles are additive, so they travel as one claim each rather than a
    // single delimited value.
    [Fact]
    public void Issue_MultipleRoles_EmitsOneRoleClaimPerRole()
    {
        var user = CreateUser(Guid.NewGuid(), Role.TenantAdmin, Role.Approver, Role.Member);

        var token = ReadToken(Issue(user));

        var roles = token.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        Assert.Equal(3, roles.Count);
        Assert.Contains(nameof(Role.TenantAdmin), roles);
        Assert.Contains(nameof(Role.Approver), roles);
        Assert.Contains(nameof(Role.Member), roles);
    }

    [Fact]
    public void Issue_ExpiryHonorsConfiguredAccessTokenMinutes()
    {
        var user = CreateUser(Guid.NewGuid(), Role.Member);

        var accessToken = Issue(user, accessTokenMinutes: 15);

        Assert.Equal(Now.AddMinutes(15), accessToken.ExpiresAtUtc);
    }

    [Fact]
    public void Issue_SetsIssuerAndAudience()
    {
        var user = CreateUser(Guid.NewGuid(), Role.Member);

        var token = ReadToken(Issue(user));

        Assert.Equal("BookSpace.Api", token.Issuer);
        Assert.Contains("BookSpace.Client", token.Audiences);
    }

    private static Application.Abstractions.AccessToken Issue(User user, int accessTokenMinutes = 15)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "BookSpace.Api",
            Audience = "BookSpace.Client",
            SigningKey = SigningKey,
            AccessTokenMinutes = accessTokenMinutes,
        });

        return new JwtAccessTokenService(options, new TestClock(Now)).Issue(user);
    }

    private static JwtSecurityToken ReadToken(Application.Abstractions.AccessToken accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken.Token);

    private static User CreateUser(Guid? orgId, params Role[] roles)
    {
        var id = Guid.NewGuid();
        var user = new User(id, orgId, "user@acme.test", "hash", "Test User", id, Now);
        foreach (var role in roles)
        {
            user.AddRole(role, id, Now);
        }

        return user;
    }
}
