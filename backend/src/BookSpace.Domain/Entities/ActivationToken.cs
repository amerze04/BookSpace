namespace BookSpace.Domain.Entities;

// What a provisioned user redeems to set their first password.
//
// `User`'s own header says there is no self-registration: every user is created
// by an administrator, with `CreatedByUserId` required. That leaves a gap this
// row closes — an administrator cannot know, or be told, the password of the
// colleague they just added, so the new account needs a credential its creator
// never sees. The plaintext of this token exists in exactly one place, the
// invitation email; only its hash is stored.
//
// **Deliberately shaped like RefreshToken next door** — SHA-256 of a CSPRNG
// value, looked up by hash, single-use, with an absolute expiry — because that
// is the shape docs/decisions/0011-refresh-token-hashing-and-rotation.md
// already argued for and the threat is the same: a bearer secret that must not
// be usable if the table is read, and must not be guessable.
//
// **No OrgId and no tenant scoping**, also like RefreshToken. Activation runs
// before the user has ever signed in, so there is no tenant context to filter
// against — the token's own secrecy is what protects it, and CLAUDE.md §4.2's
// three mechanisms have nothing to act on. The user it points at is tenant-owned
// and stays so.
//
// No audit columns, for RefreshToken's stated reason: IssuedAtUtc and
// ConsumedAtUtc already form a more precise lifecycle trail than
// CreatedAtUtc/UpdatedAtUtc would, and who provisioned the account is already
// recorded on `Users.CreatedByUserId`.
public class ActivationToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }

    public bool IsConsumed => ConsumedAtUtc is not null;

    // EF Core materialization only — see Organization.cs for why this is needed.
    private ActivationToken()
    {
        TokenHash = string.Empty;
    }

    public ActivationToken(
        Guid id,
        Guid userId,
        string tokenHash,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new ArgumentException("TokenHash is required.", nameof(tokenHash));
        if (expiresAtUtc <= issuedAtUtc)
            throw new ArgumentException("ExpiresAtUtc must be after IssuedAtUtc.", nameof(expiresAtUtc));

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    // Expiry is inclusive of the boundary the same way RefreshToken's check is
    // (`ExpiresAtUtc <= now` is expired), so the two agree about what "expired"
    // means rather than differing by a second nobody would ever find.
    public bool HasExpired(DateTime nowUtc) => ExpiresAtUtc <= nowUtc;

    public bool CanBeRedeemed(DateTime nowUtc) => !IsConsumed && !HasExpired(nowUtc);

    // Single use. The caller checks CanBeRedeemed first and reports the generic
    // failure; this throws rather than returning false because reaching it on a
    // spent token means the caller skipped that check, which is a bug and not a
    // request to refuse politely.
    //
    // The row is kept, never deleted (CLAUDE.md §4.5) — and keeping it is what
    // makes a second attempt detectable at all.
    public void Consume(DateTime nowUtc)
    {
        if (IsConsumed)
            throw new InvalidOperationException("The activation token has already been consumed.");

        ConsumedAtUtc = nowUtc;
    }
}
