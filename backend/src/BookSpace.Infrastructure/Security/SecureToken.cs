using System.Security.Cryptography;
using System.Text;

namespace BookSpace.Infrastructure.Security;

// One definition of "a bearer secret this application hands out": 256 bits from
// a CSPRNG, given to the holder Base64Url-encoded, stored only as its SHA-256.
//
// Extracted when account activation became the second user of the rule
// (RefreshTokenFactory was the first). Two copies of a crypto rule is where
// drift is most expensive — somebody shortens one, or "improves" one to a
// salted hash, and only one table's lookups break, in a way that looks like a
// data problem rather than a code change.
//
// **SHA-256 rather than PBKDF2, and that is not an inconsistency with
// PasswordHasherAdapter.** Two reasons, both from
// docs/decisions/0011-refresh-token-hashing-and-rotation.md:
//   1. Lookup happens *by* hash, against a unique index. PBKDF2 salts per call,
//      so the same token would hash differently every time and the index would
//      be unusable.
//   2. The value is already 256 bits of uniform entropy — not guessable, not
//      dictionary-attackable — so a deliberately slow KDF protects nothing. A
//      password is low-entropy, which is exactly why it gets one.
internal static class SecureToken
{
    private const int TokenBytes = 32;

    public static (string RawToken, string Hash) Create()
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return (raw, HashOf(raw));
    }

    public static string HashOf(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    // URL-safe and unpadded, so the token survives being put in a header, a
    // query string, or a JSON body without escaping. An activation token goes
    // into a link in an email, which makes this load-bearing rather than
    // merely tidy.
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
