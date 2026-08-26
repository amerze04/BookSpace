using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BookSpace.Infrastructure.Security;

// FR-2.1 short-lived access token; FR-1.4 / FR-1.5 roles travel as claims, one
// per role, because roles are additive within a tenant.
// Claim shape: docs/decisions/0009-jwt-claims-and-token-lifetimes.md.
internal sealed class JwtAccessTokenService : IAccessTokenService
{
    private readonly JwtOptions _options;
    private readonly IClock _clock;

    public JwtAccessTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public AccessToken Issue(User user)
    {
        var now = _clock.UtcNow;
        var expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
        };

        // Omitted rather than emitted empty for a SysAdmin — see BookSpaceClaims.
        if (user.OrgId is { } orgId)
        {
            claims.Add(new Claim(BookSpaceClaims.OrgId, orgId.ToString()));
        }

        // ClaimTypes.Role so [Authorize(Roles = ...)] and RequireRole work with
        // no custom claims transformation.
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role.ToString())));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

// JwtRegisteredClaimNames.Sub etc. live in JwtRegisteredClaimNames in some
// package versions and JwtClaimTypes in others; naming them here keeps this file
// independent of that churn.
internal static class JwtRegisteredClaimNames
{
    public const string Sub = "sub";
    public const string Jti = "jti";
    public const string Email = "email";
}
