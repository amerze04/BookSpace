using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(r => r.Id).HasName("PK_RefreshTokens");
        builder.HasAlternateKey(r => r.TokenHash).HasName("UQ_RefreshTokens_TokenHash");

        builder.Property(r => r.TokenHash).HasMaxLength(255).IsRequired();
        builder.Property(r => r.IssuedAtUtc).IsRequired();
        builder.Property(r => r.ExpiresAtUtc).IsRequired();
        builder.Ignore(r => r.IsActive);

        // Concurrency token, not a new column: EF appends "AND RevokedAtUtc IS NULL"
        // to the UPDATE that revokes a token, so of two requests rotating the same
        // token at once the loser affects zero rows and gets a
        // DbUpdateConcurrencyException (already mapped to 409 by
        // GlobalExceptionHandler) instead of both minting a replacement.
        // Model metadata only — no DDL, so no migration.
        builder.Property(r => r.RevokedAtUtc).IsConcurrencyToken();

        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => r.UserId)
            .HasConstraintName("FK_RefreshTokens_Users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RefreshToken>().WithMany()
            .HasForeignKey(r => r.ReplacedByTokenId)
            .HasConstraintName("FK_RefreshTokens_Replacement")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(r => r.FamilyId).HasDatabaseName("IX_RefreshTokens_Family");
    }
}
