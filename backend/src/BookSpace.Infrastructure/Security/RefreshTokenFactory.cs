using System.Security.Cryptography;
using System.Text;
using BookSpace.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Security;

// FR-2.1 / FR-2.3. 256 bits from a CSPRNG, handed to the client Base64Url-encoded;
// only the SHA-256 hash is ever stored.
//
// SHA-256 rather than PBKDF2 — the two reasons, since this looks inconsistent
// with PasswordHasherAdapter next door:
//   1. Lookup happens *by* hash against UQ_RefreshTokens_TokenHash. PBKDF2 salts
//      per call, so the same token would hash differently every time and the
//      unique index would be unusable.
//   2. The token is already 256 bits of uniform entropy, so it is not guessable
//      or dictionary-attackable and a deliberately slow KDF protects nothing.
// See docs/decisions/0011-refresh-token-hashing-and-rotation.md.
internal sealed class RefreshTokenFactory : IRefreshTokenFactory
{
    private const int TokenBytes = 32;

    public RefreshTokenFactory(IOptions<JwtOptions> options)
    {
        Lifetime = TimeSpan.FromDays(options.Value.RefreshTokenDays);
    }

    public TimeSpan Lifetime { get; }

    public GeneratedRefreshToken Create()
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return new GeneratedRefreshToken(raw, HashOf(raw));
    }

    public string HashOf(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    // URL-safe and unpadded, so the token survives being put in a header, query
    // string, or JSON body without escaping.
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
