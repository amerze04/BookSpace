using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class RecurrenceCreationOperationConfiguration : IEntityTypeConfiguration<RecurrenceCreationOperation>
{
    public void Configure(EntityTypeBuilder<RecurrenceCreationOperation> builder)
    {
        builder.ToTable("RecurrenceCreationOperations", t =>
        {
            t.HasCheckConstraint(
                "CK_RecurrenceCreationOperations_Status",
                "[Status] IN ('Creating','Active','Failed')");
        });
        builder.HasKey(o => o.Id).HasName("PK_RecurrenceCreationOperations");

        builder.Property(o => o.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(10).IsRequired();

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.CreatedByUserId).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        // One row per (tenant, caller, key) — a retry is a lookup against
        // this, not a second row.
        builder.HasIndex(o => new { o.OrgId, o.UserId, o.IdempotencyKey })
            .HasDatabaseName("UQ_RecurrenceCreationOperations_Org_User_Key")
            .IsUnique();

        builder.HasOne<Organization>().WithMany()
            .HasForeignKey(o => o.OrgId)
            .HasConstraintName("FK_RecurrenceCreationOperations_Organizations")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(o => o.UserId)
            .HasConstraintName("FK_RecurrenceCreationOperations_Users")
            .OnDelete(DeleteBehavior.NoAction);

        // No FK to RecurrenceRules: RecurrenceRuleId is cleared (MarkFailed)
        // before the rule it named is ever deleted, so nothing would enforce
        // a real invariant a NoAction FK doesn't already get from that
        // ordering — and a FK here would forbid clearing it independently of
        // the rule's own lifecycle, which RestartWith needs to do.
        builder.Property(o => o.RecurrenceRuleId);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(o => o.CreatedByUserId)
            .HasConstraintName("FK_RecurrenceCreationOperations_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(o => o.UpdatedByUserId)
            .HasConstraintName("FK_RecurrenceCreationOperations_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
