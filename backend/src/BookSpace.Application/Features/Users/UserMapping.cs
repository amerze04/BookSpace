using BookSpace.Application.Features.Users.DeactivateUser;
using BookSpace.Application.Features.Users.ReactivateUser;
using BookSpace.Application.Features.Users.ReplaceUserRoles;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Users;

// Entity -> response, one mapper per endpoint, mirroring ResourceMapping.
//
// The three are field-for-field identical today and are still written out
// separately, because the types they build are separate on purpose (decision
// `0015`). A shared mapper would be the back door that couples the three
// contracts the types were kept apart to decouple.
internal static class UserMapping
{
    public static DeactivateUserCommandResponse ToDeactivateResponse(this User user) =>
        new(user.Id, user.Email, user.FullName, user.IsActive, Roles(user), user.UpdatedAtUtc);

    public static ReactivateUserCommandResponse ToReactivateResponse(this User user) =>
        new(user.Id, user.Email, user.FullName, user.IsActive, Roles(user), user.UpdatedAtUtc);

    public static ReplaceUserRolesCommandResponse ToRolesResponse(this User user) =>
        new(user.Id, user.Email, user.FullName, user.IsActive, Roles(user), user.UpdatedAtUtc);

    // Ordered, because User.Roles projects an owned collection whose order is
    // whatever the change tracker or SQL Server happened to produce — so two
    // identical role sets could serialize differently and a client diffing
    // responses would see a change that did not happen.
    private static IReadOnlyList<Domain.Enums.Role> Roles(User user) =>
        user.Roles.OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();
}
