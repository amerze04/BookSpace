using BookSpace.Application.Abstractions;
using BookSpace.Domain.Enums;

namespace BookSpace.Api.Tenancy;

// Reads the `sub` claim JwtAccessTokenService mints
// (docs/decisions/0009-jwt-claims-and-token-lifetimes.md). Program.cs sets
// MapInboundClaims = false, so `sub` arrives as `sub` rather than being
// remapped to the long ClaimTypes.NameIdentifier URI — this reads back exactly
// what was issued.
//
// Every missing-or-unparseable branch resolves to null rather than throwing,
// for the same reason HttpContextCurrentTenant does: null is a meaningful state
// (no request, anonymous endpoint, background work), and it is the caller that
// knows whether null is acceptable. The write handlers treat it as a wiring
// bug; see CreateResourceCommandRequestHandler.
internal sealed class HttpContextCurrentUser : ICurrentUser
{
    private const string SubClaim = "sub";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User?.FindFirst(SubClaim)?.Value;
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }

    // ClaimsPrincipal.IsInRole, which reads whichever claim type
    // TokenValidationParameters.RoleClaimType names — Program.cs sets it to
    // ClaimTypes.Role, the same type JwtAccessTokenService emits one claim per
    // role into. So this and RequireRole in AuthorizationPolicies read exactly
    // the same claims, and a role that satisfies a policy cannot fail here.
    //
    // role.ToString() is the enum's name, which is also how the token spells it
    // (`nameof(Role.TenantAdmin)` in the policies, `role.ToString()` in the
    // token service). The three agree because all three derive from the enum
    // rather than from a literal.
    //
    // No principal means no roles, not an exception: an anonymous or absent
    // context answers false to every role, so a caller can never be treated as
    // privileged by a missing HttpContext.
    public bool IsInRole(Role role) =>
        _httpContextAccessor.HttpContext?.User?.IsInRole(role.ToString()) ?? false;
}
