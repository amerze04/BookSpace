using BookSpace.Domain.Enums;

namespace BookSpace.Application.Abstractions;

// The acting user's id, read from the `sub` claim
// (docs/decisions/0009-jwt-claims-and-token-lifetimes.md). Needed by every
// write path, because IAuditable's CreatedByUserId/UpdatedByUserId are not
// nullable-by-convenience — the schema requires knowing who did it.
//
// Companion to ICurrentTenant, implemented in BookSpace.Api for the same
// reason: reading HttpContext.User is an ASP.NET Core concern, and
// BookSpace.Infrastructure has no dependency on it.
//
// Null means no authenticated user — outside a request (SeedData, migrations,
// a background job) or on an anonymous endpoint. A write handler treats null as
// a wiring bug rather than a client error: its endpoints sit behind the
// TenantAdmin policy, so a request that reached one has a principal.
public interface ICurrentUser
{
    Guid? UserId { get; }

    // Whether the caller holds a role, for the handful of rules that depend on
    // *which* member is asking rather than merely that one is (WP-4 Phase 2a).
    //
    // **Why the Application layer needs this at all**, when RBAC has otherwise
    // been entirely declarative on the controllers: decision 0002 gives a
    // TenantAdmin the right to see and cancel bookings they do not own, and
    // records explicitly that the check "belongs in the Application layer, not
    // the Domain entity". It cannot be a policy on the action, because the same
    // route serves both actors and the difference is in *which rows* are
    // visible, not in whether the route may be called. A separate admin route
    // would give one booking two URLs and split the 404-not-403 rule across
    // them (see BookingNotFoundException).
    //
    // Takes the Domain enum rather than a string, so a handler cannot mistype a
    // role name and silently never match. The implementation converts to the
    // claim spelling in one place, which is the same value
    // JwtAccessTokenService emits and AuthorizationPolicies requires.
    //
    // False when there is no authenticated user, matching UserId's null: a
    // caller with no principal holds no roles. Handlers therefore never get a
    // "privileged by default" answer out of this.
    bool IsInRole(Role role);
}
