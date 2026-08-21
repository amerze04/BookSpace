namespace BookSpace.Domain.Entities;

// FR-2.1 rotating refresh tokens; FR-2.2 reuse kills the family;
// FR-2.3 hash only, never the raw token.
// No generic audit columns here (docs/decisions trim decision, see
// docs/bookspace-schema-v2.sql): IssuedAtUtc / RevokedAtUtc /
// ReplacedByTokenId already form a complete, more precise lifecycle trail
// than CreatedAtUtc/UpdatedAtUtc would add, and UserId is already the owner.
public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public Guid FamilyId { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public RefreshToken(
        Guid id,
        Guid userId,
        string tokenHash,
        Guid familyId,
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
        FamilyId = familyId;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    // FR-2.2: reuse of an already-rotated/revoked token kills the whole family.
    public void Revoke(DateTime nowUtc, Guid? replacedByTokenId = null)
    {
        RevokedAtUtc = nowUtc;
        ReplacedByTokenId = replacedByTokenId;
    }
}
