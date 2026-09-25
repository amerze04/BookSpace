using System.Security.Claims;
using BookSpace.Api.Authorization;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace BookSpace.UnitTests.Authorization;

// Hardening pass, 2026-09-25 (finding 1). The fast, DB-free half of the proof
// — the handler's own decision table. What a fake cannot prove is that a real
// token issued before a real deactivation is actually refused end to end;
// that is StaleAdminTokenAuthorizationTests, against a real host and real
// issued tokens.
public class ActiveTenantAdminAuthorizationHandlerTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly FakeUserRepository _users = new();

    [Fact]
    public async Task SucceedsForAnActiveTenantAdmin()
    {
        var admin = User(Role.TenantAdmin);

        var context = await EvaluateAsync(admin.Id);

        Assert.True(context.HasSucceeded);
    }

    // The first repro scenario's shape: the row says deactivated, the token
    // (not modelled here — see the integration test) still says TenantAdmin.
    [Fact]
    public async Task FailsForADeactivatedAdmin()
    {
        var admin = User(Role.TenantAdmin);
        admin.Deactivate(ActorId, NowUtc);

        var context = await EvaluateAsync(admin.Id);

        Assert.False(context.HasSucceeded);
    }

    // The second repro scenario's shape: TenantAdmin was taken off the row.
    [Fact]
    public async Task FailsWhenTheRoleHasBeenRemoved()
    {
        var former = User(Role.Member);

        var context = await EvaluateAsync(former.Id);

        Assert.False(context.HasSucceeded);
    }

    // A row that never existed in this tenant answers the same as one that
    // used to — AC-4's fail-closed shape applied to the actor rather than to a
    // resource being read.
    [Fact]
    public async Task FailsForAnUnknownUserId()
    {
        User(Role.TenantAdmin); // someone else exists; the id under test does not.

        var context = await EvaluateAsync(Guid.NewGuid());

        Assert.False(context.HasSucceeded);
    }

    // Unreachable behind TenantMember + TenantAdmin in production, but the
    // handler must not assume it always runs after them.
    [Fact]
    public async Task FailsWhenThereIsNoAuthenticatedUser()
    {
        var context = await EvaluateAsync(userId: null);

        Assert.False(context.HasSucceeded);
    }

    // Approver alone is not TenantAdmin — the DB check has to name the role,
    // not just "some role exists".
    [Fact]
    public async Task FailsForAnApproverWhoIsNotAlsoAnAdmin()
    {
        var approver = User(Role.Approver, Role.Member);

        var context = await EvaluateAsync(approver.Id);

        Assert.False(context.HasSucceeded);
    }

    private async Task<AuthorizationHandlerContext> EvaluateAsync(Guid? userId)
    {
        var handler = new ActiveTenantAdminAuthorizationHandler(
            new FixedCurrentUser(userId), _users, new NullHttpContextAccessor());
        var requirement = new ActiveTenantAdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), resource: null);

        await handler.HandleAsync(context);
        return context;
    }

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

    // The handler only ever reads .HttpContext?.RequestAborted, and a null
    // HttpContext is a real, expected state to fall back from (CancellationToken.None).
    private sealed class NullHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set { }
        }
    }
}
