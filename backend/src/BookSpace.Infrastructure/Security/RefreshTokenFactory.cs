using BookSpace.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Security;

// FR-2.1 / FR-2.3. 256 bits from a CSPRNG, handed to the client Base64Url-encoded;
// only the SHA-256 hash is ever stored.
//
// The generation and hashing themselves moved to SecureToken when account
// activation became the second caller of the same rule — see that file for why
// it is SHA-256 here and PBKDF2 next door in PasswordHasherAdapter, and
// docs/decisions/0011-refresh-token-hashing-and-rotation.md for the decision.
// What stays here is the part that is specific to refresh tokens: their
// lifetime.
internal sealed class RefreshTokenFactory : IRefreshTokenFactory
{
    public RefreshTokenFactory(IOptions<JwtOptions> options)
    {
        Lifetime = TimeSpan.FromDays(options.Value.RefreshTokenDays);
    }

    public TimeSpan Lifetime { get; }

    public GeneratedRefreshToken Create()
    {
        var (raw, hash) = SecureToken.Create();
        return new GeneratedRefreshToken(raw, hash);
    }

    public string HashOf(string rawToken) => SecureToken.HashOf(rawToken);
}
