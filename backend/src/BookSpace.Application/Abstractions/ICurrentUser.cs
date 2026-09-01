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
}
