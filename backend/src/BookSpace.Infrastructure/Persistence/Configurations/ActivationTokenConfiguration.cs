using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

// Deliberately shaped like RefreshTokenConfiguration, including what is absent:
// no OrgId, no global query filter, and no entry in
// Security.TenantAccessPolicy. Activation runs before the user has ever signed
// in, so there is no tenant context for CLAUDE.md §4.2's three mechanisms to
// act on — the token's own secrecy is the access control, exactly as it is for
// a refresh token.
internal sealed class ActivationTokenConfiguration : IEntityTypeConfiguration<ActivationToken>
{
    public void Configure(EntityTypeBuilder<ActivationToken> builder)
    {
        builder.ToTable("ActivationTokens");
        builder.HasKey(t => t.Id).HasName("PK_ActivationTokens");

        // An alternate key, not just an index: the lookup is *by* hash, and two
        // rows sharing one would make "which account does this token belong to"
        // ambiguous. Matches UQ_RefreshTokens_TokenHash.
        builder.HasAlternateKey(t => t.TokenHash).HasName("UQ_ActivationTokens_TokenHash");

        builder.Property(t => t.TokenHash).HasMaxLength(255).IsRequired();
        builder.Property(t => t.IssuedAtUtc).IsRequired();
        builder.Property(t => t.ExpiresAtUtc).IsRequired();
        builder.Ignore(t => t.IsConsumed);
        builder.Ignore(t => t.IsSuperseded);

        // Concurrency token, not a new column, and the same trick
        // RefreshTokens.RevokedAtUtc uses: EF appends "AND ConsumedAtUtc IS
        // NULL" to the UPDATE that consumes a token, so of two requests
        // redeeming the same invitation at once the loser affects zero rows and
        // gets a DbUpdateConcurrencyException (already mapped to 409 by
        // GlobalExceptionHandler) instead of both setting a password. That is
        // what makes "single use" true under concurrency rather than only in
        // sequence. Model metadata only — no DDL.
        builder.Property(t => t.ConsumedAtUtc).IsConcurrencyToken();

        // Hardening pass, 2026-09-25 (findings 2/3). Also a concurrency token,
        // and deliberately paired with ConsumedAtUtc rather than standing
        // alone: EF includes *every* concurrency-token property's original
        // value in an UPDATE's WHERE clause regardless of which one actually
        // changed, so an activation racing a reissue of the same token has the
        // same "loser affects zero rows, surfaces as 409" outcome the two
        // simultaneous activations case above already has — one consistent
        // answer for "something about this token changed under you" rather
        // than a second, differently-shaped race with no guard at all.
        builder.Property(t => t.SupersededAtUtc).IsConcurrencyToken();

        // Cascade, like RefreshTokens: a token is meaningless without the user
        // it activates, and this is a single-path cascade (CLAUDE.md §5). In
        // practice nothing deletes a user (§4.5), so it never fires — it is
        // here so the shape of the relationship is stated rather than left to a
        // default.
        builder.HasOne<User>().WithMany()
            .HasForeignKey(t => t.UserId)
            .HasConstraintName("FK_ActivationTokens_Users")
            .OnDelete(DeleteBehavior.Cascade);

        // Not unique: a user may legitimately accumulate rows here once
        // re-issuing an invitation exists (docs/user-management-plan.md §6).
        // Today there is exactly one per user, and the index is what makes
        // "show me this account's invitations" cheap rather than a scan.
        builder.HasIndex(t => t.UserId).HasDatabaseName("IX_ActivationTokens_User");
    }
}
