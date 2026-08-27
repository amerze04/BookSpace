namespace BookSpace.Application.Abstractions;

// FR-2.1 / FR-2.3. The raw token is handed to the client once and never stored;
// only Hash reaches the database. Deliberately a fast deterministic hash rather
// than a password KDF — see docs/decisions/0011-refresh-token-hashing-and-rotation.md.
public sealed record GeneratedRefreshToken(string RawToken, string Hash);

public interface IRefreshTokenFactory
{
    // How long a *family* lives, applied once at login. Rotation inherits the
    // original expiry rather than restarting this clock, so the window is
    // absolute and a stolen token can't be renewed indefinitely.
    TimeSpan Lifetime { get; }

    GeneratedRefreshToken Create();

    // Same hash function as Create, used to look a presented token up by hash.
    string HashOf(string rawToken);
}
