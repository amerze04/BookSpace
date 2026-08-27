using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace BookSpace.Infrastructure.Security;

// FR-2.3. Wraps ASP.NET Core Identity's PasswordHasher<T> (PBKDF2-HMAC-SHA512,
// 100k iterations, salt and parameters encoded into the output) so the Identity
// package stays out of the Application layer (CLAUDE.md §3).
//
// PBKDF2 is right here and deliberately *not* used for refresh tokens: a
// password is low-entropy and guessable, so the cost factor is the whole point.
// See docs/decisions/0011-refresh-token-hashing-and-rotation.md.
internal sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();

    public string Hash(string password) => _inner.HashPassword(user: null!, password);

    public bool Verify(string passwordHash, string password)
    {
        // PasswordHasher throws on a malformed stored hash (e.g. the WP-1 seed
        // placeholder) rather than returning Failed — treat that as "does not
        // verify" so one bad row can't 500 the login endpoint.
        try
        {
            var result = _inner.VerifyHashedPassword(user: null!, passwordHash, password);
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
