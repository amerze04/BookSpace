using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class OrganizationTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static Organization CreateValid() =>
        new(Guid.NewGuid(), "Acme Corporation", "acme", "America/New_York", 60, 15, 24, ActorId, NowUtc);

    [Fact]
    public void Constructor_SetsExpectedDefaults()
    {
        var org = CreateValid();

        Assert.Equal(OrganizationStatus.Active, org.Status);
        Assert.Equal(NowUtc, org.CreatedAtUtc);
        Assert.Equal(NowUtc, org.UpdatedAtUtc);
        Assert.Equal(ActorId, org.CreatedByUserId);
        Assert.Equal(ActorId, org.UpdatedByUserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankName(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new Organization(Guid.NewGuid(), name, "acme", "America/New_York", 60, 15, 24, ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankSlug(string slug)
    {
        Assert.Throws<ArgumentException>(() =>
            new Organization(Guid.NewGuid(), "Acme", slug, "America/New_York", 60, 15, 24, ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankTimeZoneId(string timeZoneId)
    {
        Assert.Throws<ArgumentException>(() =>
            new Organization(Guid.NewGuid(), "Acme", "acme", timeZoneId, 60, 15, 24, ActorId, NowUtc));
    }

    [Fact]
    public void Suspend_SetsStatusAndAuditFields()
    {
        var org = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddDays(1);

        org.Suspend(actor, later);

        Assert.Equal(OrganizationStatus.Suspended, org.Status);
        Assert.Equal(actor, org.UpdatedByUserId);
        Assert.Equal(later, org.UpdatedAtUtc);
    }

    [Fact]
    public void Reactivate_AfterSuspend_RestoresActiveStatus()
    {
        var org = CreateValid();
        var actor = Guid.NewGuid();

        org.Suspend(actor, NowUtc.AddDays(1));
        org.Reactivate(actor, NowUtc.AddDays(2));

        Assert.Equal(OrganizationStatus.Active, org.Status);
    }
}
