using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Users.GetUserById;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;

namespace BookSpace.UnitTests.Users;

// GET /users/{id} — user management phase 7. A thin handler (FindDetailAsync
// does the actual work, and the repository's own SQL is what
// GetUserByIdEndpointTests proves against a real tenant boundary), so what is
// worth covering here is the one branch: found vs. not.
public class GetUserByIdHandlerTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly FakeUserRepository _users = new();

    [Fact]
    public async Task ReturnsTheUsersDetail()
    {
        var user = User(Role.Approver, Role.TenantAdmin);

        var response = await GetById(user.Id);

        Assert.Equal(user.Id, response.Id);
        Assert.Equal(user.FullName, response.FullName);
        Assert.Equal(user.Email, response.Email);
        Assert.True(response.IsActive);
        Assert.Equal([Role.Approver, Role.TenantAdmin], response.Roles.OrderBy(r => r.ToString()));
        Assert.Equal(user.CreatedAtUtc, response.CreatedAtUtc);
        Assert.Equal(user.UpdatedAtUtc, response.UpdatedAtUtc);
        Assert.False(response.IsActivated);
    }

    // Hardening pass, 2026-09-25 (finding 3) — what the detail screen uses to
    // decide whether "Resend invitation" applies at all.
    [Fact]
    public async Task ReportsWhenTheAccountHasBeenActivated()
    {
        var user = User(Role.Member);
        _users.ActivatedUserIds.Add(user.Id);

        var response = await GetById(user.Id);

        Assert.True(response.IsActivated);
    }

    // A deactivated user is still readable by id — only the directory's default
    // view hides one, and this is not that.
    [Fact]
    public async Task ADeactivatedUserIsStillReadable()
    {
        var user = User(Role.Member);
        user.Deactivate(ActorId, NowUtc);

        var response = await GetById(user.Id);

        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task AnUnknownIdIsNotFound()
    {
        User(Role.TenantAdmin);

        var exception = await Assert.ThrowsAsync<UserNotFoundException>(() => GetById(Guid.NewGuid()));

        Assert.Equal(ReasonCodes.UserNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    // ---- Helpers ----

    private Task<GetUserByIdQueryResponse> GetById(Guid userId) =>
        new GetUserByIdQueryRequestHandler(_users)
            .Handle(new GetUserByIdQueryRequest(userId), CancellationToken.None);

    private User User(params Role[] roles)
    {
        var user = new User(
            Guid.NewGuid(),
            OrgId,
            $"user-{Guid.NewGuid():N}@acme.test",
            "hash",
            "A Person",
            ActorId,
            NowUtc.AddDays(-7));

        foreach (var role in roles)
        {
            user.AddRole(role, ActorId, NowUtc.AddDays(-7));
        }

        _users.Users.Add(user);
        return user;
    }
}
