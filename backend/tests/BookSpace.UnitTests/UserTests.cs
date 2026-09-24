using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class UserTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static User CreateValid(Guid? orgId = null) =>
        new(Guid.NewGuid(), orgId, "member@acme.test", "hash", "Member One", ActorId, NowUtc);

    [Fact]
    public void Constructor_SetsExpectedDefaults()
    {
        var user = CreateValid();

        Assert.True(user.IsActive);
        Assert.NotEqual(Guid.Empty, user.CalendarFeedToken);
        Assert.Empty(user.Roles);
        Assert.Equal(NowUtc, user.CreatedAtUtc);
    }

    [Fact]
    public void Constructor_AllowsNullOrgId_ForSysAdmin()
    {
        var user = CreateValid(orgId: null);

        Assert.Null(user.OrgId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankEmail(string email)
    {
        Assert.Throws<ArgumentException>(() =>
            new User(Guid.NewGuid(), null, email, "hash", "Name", ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankPasswordHash(string passwordHash)
    {
        Assert.Throws<ArgumentException>(() =>
            new User(Guid.NewGuid(), null, "a@b.test", passwordHash, "Name", ActorId, NowUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankFullName(string fullName)
    {
        Assert.Throws<ArgumentException>(() =>
            new User(Guid.NewGuid(), null, "a@b.test", "hash", fullName, ActorId, NowUtc));
    }

    [Fact]
    public void AddRole_AddsRoleAndTouchesAuditFields()
    {
        var user = CreateValid();
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        user.AddRole(Role.Member, actor, later);

        Assert.Contains(Role.Member, user.Roles);
        Assert.Equal(actor, user.UpdatedByUserId);
        Assert.Equal(later, user.UpdatedAtUtc);
    }

    [Fact]
    public void AddRole_IsIdempotent_AndDoesNotTouchOnDuplicate()
    {
        var user = CreateValid();
        var firstActor = Guid.NewGuid();
        user.AddRole(Role.Member, firstActor, NowUtc.AddMinutes(1));

        var secondActor = Guid.NewGuid();
        user.AddRole(Role.Member, secondActor, NowUtc.AddMinutes(2));

        Assert.Single(user.Roles);
        Assert.Equal(firstActor, user.UpdatedByUserId);
    }

    [Fact]
    public void RemoveRole_RemovesRoleAndTouchesAuditFields()
    {
        var user = CreateValid();
        user.AddRole(Role.Approver, ActorId, NowUtc);
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(10);

        user.RemoveRole(Role.Approver, actor, later);

        Assert.DoesNotContain(Role.Approver, user.Roles);
        Assert.Equal(actor, user.UpdatedByUserId);
        Assert.Equal(later, user.UpdatedAtUtc);
    }

    [Fact]
    public void RemoveRole_DoesNotTouch_WhenRoleNotPresent()
    {
        var user = CreateValid();
        var actor = Guid.NewGuid();

        user.RemoveRole(Role.Approver, actor, NowUtc.AddMinutes(1));

        Assert.NotEqual(actor, user.UpdatedByUserId);
    }

    [Fact]
    public void Deactivate_SetsIsActiveFalse()
    {
        var user = CreateValid();

        user.Deactivate(ActorId, NowUtc.AddMinutes(1));

        Assert.False(user.IsActive);
    }

    [Fact]
    public void Reactivate_SetsIsActiveTrue()
    {
        var user = CreateValid();
        user.Deactivate(ActorId, NowUtc.AddMinutes(1));

        user.Reactivate(ActorId, NowUtc.AddMinutes(2));

        Assert.True(user.IsActive);
    }

    // User management phase 2. The only way PasswordHash changes after
    // construction — see ActivationToken for why it has to change at all.
    [Fact]
    public void SetPassword_ReplacesTheHash()
    {
        var user = CreateValid();

        user.SetPassword("new-hash", ActorId, NowUtc.AddMinutes(1));

        Assert.Equal("new-hash", user.PasswordHash);
    }

    // On activation the actor is the user themselves, which is the one moment
    // in this system somebody acts on their own account before ever signing in.
    [Fact]
    public void SetPassword_RecordsWhoChangedItAndWhen()
    {
        var user = CreateValid();
        var changedAt = NowUtc.AddMinutes(5);

        user.SetPassword("new-hash", user.Id, changedAt);

        Assert.Equal(changedAt, user.UpdatedAtUtc);
        Assert.Equal(user.Id, user.UpdatedByUserId);
    }

    // The same guard the constructor has. A blank hash would make every login
    // attempt against the account fail in a way that looks like a wrong
    // password rather than a corrupted row.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetPassword_ThrowsOnBlankHash(string passwordHash)
    {
        var user = CreateValid();

        Assert.Throws<ArgumentException>(() => user.SetPassword(passwordHash, ActorId, NowUtc));
    }

    [Fact]
    public void SetPassword_LeavesEverythingElseAlone()
    {
        var user = CreateValid(orgId: Guid.NewGuid());
        user.AddRole(Role.Approver, ActorId, NowUtc);
        var orgId = user.OrgId;
        var feedToken = user.CalendarFeedToken;

        user.SetPassword("new-hash", user.Id, NowUtc.AddMinutes(1));

        Assert.Equal(orgId, user.OrgId);
        Assert.Equal(feedToken, user.CalendarFeedToken);
        Assert.True(user.IsActive);
        Assert.Contains(Role.Approver, user.Roles);
    }
}
