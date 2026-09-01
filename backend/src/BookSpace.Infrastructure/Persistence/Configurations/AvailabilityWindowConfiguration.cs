using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class AvailabilityWindowConfiguration : IEntityTypeConfiguration<AvailabilityWindow>
{
    public void Configure(EntityTypeBuilder<AvailabilityWindow> builder)
    {
        builder.ToTable("AvailabilityWindows", t =>
        {
            t.HasCheckConstraint("CK_AvailabilityWindows_Weekday", "[Weekday] BETWEEN 0 AND 6");
            t.HasCheckConstraint("CK_AvailabilityWindows_Window", "[ClosesAt] > [OpensAt]");
        });
        builder.HasKey(w => w.Id).HasName("PK_AvailabilityWindows");

        builder.Property(w => w.OrgId).IsRequired();
        builder.Property(w => w.Weekday).HasConversion<byte>().IsRequired();
        builder.Property(w => w.OpensAt).HasPrecision(0).IsRequired();
        builder.Property(w => w.ClosesAt).HasPrecision(0).IsRequired();

        // The FK itself ((OrgId, ResourceId) -> Resources) is configured from
        // the Resource side in ResourceConfiguration.cs, alongside the
        // AvailabilityWindows navigation's field access.
    }
}
