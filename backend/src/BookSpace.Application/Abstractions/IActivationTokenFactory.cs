namespace BookSpace.Application.Abstractions;

// The raw token goes into the invitation email and is never stored; only Hash
// reaches the database (ActivationToken). Same shape as GeneratedRefreshToken,
// and for the same reason — see
// docs/decisions/0011-refresh-token-hashing-and-rotation.md.
public sealed record GeneratedActivationToken(string RawToken, string Hash);

// A separate port from IRefreshTokenFactory even though the two produce the
// same kind of value, because they answer to different policy: an activation
// token's lifetime is an invitation window measured in days, a refresh token's
// is a session window, and one interface with both would make the wrong
// Lifetime reachable from the wrong caller. The generation and hashing
// themselves are shared (Infrastructure/Security/SecureToken).
public interface IActivationTokenFactory
{
    // How long an invitation stays redeemable, from issue. Absolute: nothing
    // extends it, and there is no way to re-issue one today
    // (docs/user-management-plan.md §6).
    TimeSpan Lifetime { get; }

    GeneratedActivationToken Create();

    // The same hash function as Create, used to look a presented token up by
    // hash — which is why it has to be deterministic. See SecureToken.
    string HashOf(string rawToken);
}
