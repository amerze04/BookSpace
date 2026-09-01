using BookSpace.Application.Abstractions;

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
// bug; see CreateResourceCommandHandler.
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
}
