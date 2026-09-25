using BookSpace.Application.Features.Users.DeactivateUser;
using BookSpace.Application.Features.Users.GetUserById;
using BookSpace.Application.Features.Users.ReactivateUser;
using BookSpace.Application.Features.Users.ReplaceUserRoles;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests.Users;

// Shape rules for the three phase-5 writes. One of them is a security control
// rather than a formatting rule — see the SysAdmin tests — and it has no reason
// code, so this file is the only place it is written down in executable form.
public class UserWriteValidatorTests
{
    private readonly ReplaceUserRolesCommandRequestValidator _roles = new();

    [Fact]
    public void AWellFormedRoleRequestIsValid()
    {
        Assert.True(_roles.Validate(Request([Role.Member])).IsValid);
    }

    [Fact]
    public void SeveralRolesAreValid()
    {
        Assert.True(_roles.Validate(Request([Role.TenantAdmin, Role.Approver, Role.Member])).IsValid);
    }

    [Fact]
    public void AnEmptyUserIdIsInvalid()
    {
        Assert.False(_roles.Validate(new ReplaceUserRolesCommandRequest(Guid.Empty, [Role.Member])).IsValid);
        Assert.False(new DeactivateUserCommandRequestValidator()
            .Validate(new DeactivateUserCommandRequest(Guid.Empty)).IsValid);
        Assert.False(new ReactivateUserCommandRequestValidator()
            .Validate(new ReactivateUserCommandRequest(Guid.Empty)).IsValid);
        Assert.False(new GetUserByIdQueryRequestValidator()
            .Validate(new GetUserByIdQueryRequest(Guid.Empty)).IsValid);
    }

    [Fact]
    public void ARealUserIdIsValidOnTheTwoStateChanges()
    {
        Assert.True(new DeactivateUserCommandRequestValidator()
            .Validate(new DeactivateUserCommandRequest(Guid.NewGuid())).IsValid);
        Assert.True(new ReactivateUserCommandRequestValidator()
            .Validate(new ReactivateUserCommandRequest(Guid.NewGuid())).IsValid);
        Assert.True(new GetUserByIdQueryRequestValidator()
            .Validate(new GetUserByIdQueryRequest(Guid.NewGuid())).IsValid);
    }

    // **Unlike the approver list next door, which takes an empty array as a real
    // request.** A resource with no approvers is a coherent state (decision
    // `0028` made it a supported one); a user with no roles is not, because
    // AuthorizationPolicies.TenantMember needs only the orgId claim — so they
    // keep full member access and the row would lie about it. Same reason
    // POST /users assigns Member rather than nothing.
    [Fact]
    public void AnEmptyRoleSetIsInvalid()
    {
        Assert.False(_roles.Validate(Request([])).IsValid);
    }

    [Fact]
    public void AnEmptyRoleSetSaysWhatToDoInstead()
    {
        var message = Assert.Single(_roles.Validate(Request([])).Errors).ErrorMessage;

        Assert.Contains("deactivate", message, StringComparison.OrdinalIgnoreCase);
    }

    // **A privilege-escalation guard.** SysAdmin is the platform operator and is
    // deliberately not a tenant role (PRD §2, decision `0012`). The database's
    // CK_UserRoles_Role allows the value — the bootstrap SysAdmin row needs it —
    // so this validator is the only thing standing between a TenantAdmin and the
    // platform role.
    [Fact]
    public void AssigningSysAdminIsRefused()
    {
        Assert.False(_roles.Validate(Request([Role.SysAdmin])).IsValid);
    }

    [Fact]
    public void SmugglingSysAdminAlongsideALegitimateRoleIsRefused()
    {
        Assert.False(_roles.Validate(Request([Role.TenantAdmin, Role.SysAdmin])).IsValid);
    }

    [Fact]
    public void TheSysAdminRefusalSaysWhy()
    {
        var messages = _roles.Validate(Request([Role.SysAdmin])).Errors.Select(e => e.ErrorMessage);

        Assert.Contains(messages, m => m.Contains("platform role", StringComparison.OrdinalIgnoreCase));
    }

    // Bound by name from the body, so an unrecognized word never reaches here.
    // The numeric form is what this catches: an undefined Role would be stored
    // as a string CK_UserRoles_Role then rejects — a 500 where a 400 belongs.
    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void AnUndefinedRoleIsInvalid(int role)
    {
        Assert.False(_roles.Validate(Request([(Role)role])).IsValid);
    }

    // Rejected rather than collapsed: the domain applies set semantics, so a
    // repeated role would store cleanly and the response would silently contain
    // fewer entries than the request (docs/decisions/0015).
    [Fact]
    public void DuplicateRolesAreInvalid()
    {
        Assert.False(_roles.Validate(Request([Role.Member, Role.Member])).IsValid);
    }

    private static ReplaceUserRolesCommandRequest Request(IReadOnlyList<Role> roles) =>
        new(Guid.NewGuid(), roles);
}
