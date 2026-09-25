using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users;

// The last-admin guard, in one place because two write paths can violate the
// same invariant and each reason code gets exactly one thrower — the shape
// ResourceWriteRules established.
//
// **The invariant: a tenant always has at least one active TenantAdmin.**
// docs/user-management-plan.md §3.3 for why it is worth enforcing at all (a
// tenant with none cannot be managed by any API here, and decision `0028` routes
// approval notifications to exactly that set), and §4.5 for why it cannot be a
// plain read-check-write.
internal static class UserWriteRules
{
    // Called before deactivating. Only a currently-active TenantAdmin can empty
    // the set by being deactivated — somebody already inactive is not in it, and
    // somebody without the role never was.
    public static Task EnsureNotTheLastAdminBeingDeactivatedAsync(
        IUserRepository users,
        User user,
        CancellationToken cancellationToken) =>
        user is { IsActive: true } && user.Roles.Contains(Role.TenantAdmin)
            ? EnsureAnotherAdminRemainsAsync(users, user, cancellationToken)
            : Task.CompletedTask;

    // Called before replacing a role set. The set shrinks by one admin only if
    // the user is active, holds the role now, and will not after — so a
    // deactivated admin's roles can be edited freely, and adding roles never
    // trips this.
    public static Task EnsureNotTheLastAdminLosingTheRoleAsync(
        IUserRepository users,
        User user,
        IReadOnlyCollection<Role> desiredRoles,
        CancellationToken cancellationToken) =>
        user is { IsActive: true }
            && user.Roles.Contains(Role.TenantAdmin)
            && !desiredRoles.Contains(Role.TenantAdmin)
                ? EnsureAnotherAdminRemainsAsync(users, user, cancellationToken)
                : Task.CompletedTask;

    // **This is the locking read, and it has to run inside the same transaction
    // as the write** — see IUserRepository.CountOtherActiveTenantAdminsAsync.
    // Calling it outside one would take the lock and release it immediately,
    // which looks identical in every test that runs one request at a time and
    // is worth nothing under two.
    private static async Task EnsureAnotherAdminRemainsAsync(
        IUserRepository users,
        User user,
        CancellationToken cancellationToken)
    {
        var others = await users.CountOtherActiveTenantAdminsAsync(user.Id, cancellationToken);

        if (others == 0)
        {
            throw new LastTenantAdminException();
        }
    }
}
