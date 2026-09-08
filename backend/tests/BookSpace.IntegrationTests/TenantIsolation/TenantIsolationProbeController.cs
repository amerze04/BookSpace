using BookSpace.Api.Authorization;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.IntegrationTests.TenantIsolation;

// Test-only, modeled on Authentication/PolicyProbeController. The API has no
// real Resources/Users read endpoints yet (Phase 4 lands before those
// features), so this gives AC-4 something concrete to assert against —
// deliberately runs no IgnoreQueryFilters() of its own, so a request through
// here exercises the real global query filter and RLS layer exactly as a
// production endpoint would. Registered the same way PolicyProbeController
// is, via AuthenticationTestHost's AddApplicationPart. Must never ship in
// BookSpace.Api.
[ApiController]
[Route("test-probe-tenant")]
public sealed class TenantIsolationProbeController : ControllerBase
{
    [HttpGet("resources")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> Resources([FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var resources = await db.Resources
            .Select(r => new { r.Id, r.OrgId, r.Name })
            .ToListAsync(cancellationToken);
        return Ok(resources);
    }

    [HttpGet("resources/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> ResourceById(Guid id, [FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var resource = await db.Resources
            .Where(r => r.Id == id)
            .Select(r => new { r.Id, r.OrgId, r.Name })
            .FirstOrDefaultAsync(cancellationToken);
        return resource is null ? NotFound() : Ok(resource);
    }

    // WP-3 decision D1. Written the way a handler naturally would be before D1
    // — filter by Id alone, no OrgId anywhere — which is exactly the shape the
    // plan flagged as a cross-tenant read. It is safe now only because the
    // query filter and RLS make it so.
    [HttpGet("blackouts/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> BlackoutById(Guid id, [FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var blackout = await db.BlackoutPeriods
            .Where(b => b.Id == id)
            .Select(b => new { b.Id, b.OrgId, b.ResourceId })
            .FirstOrDefaultAsync(cancellationToken);
        return blackout is null ? NotFound() : Ok(blackout);
    }

    // WP-4 Phase 3. Written the same deliberately naive way as the blackout
    // probe above — filter by Id alone, no OrgId and no owner check anywhere —
    // because that is the shape a handler takes before anyone thinks about
    // isolation. It is safe only because the query filter and RLS make it so,
    // which is precisely what the tests over it assert. The real GET
    // /bookings/{id} additionally applies an owner filter (decision 0002); this
    // probe deliberately does not, so a failure here can only be a *tenant*
    // leak.
    [HttpGet("bookings/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> BookingById(Guid id, [FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .Where(b => b.Id == id)
            .Select(b => new { b.Id, b.OrgId, b.ResourceId, b.UserId })
            .FirstOrDefaultAsync(cancellationToken);
        return booking is null ? NotFound() : Ok(booking);
    }

    [HttpGet("bookings")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> Bookings([FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var bookings = await db.Bookings
            .Select(b => new { b.Id, b.OrgId, b.ResourceId, b.UserId })
            .ToListAsync(cancellationToken);
        return Ok(bookings);
    }

    [HttpGet("availability-windows")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> AvailabilityWindows([FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var windows = await db.AvailabilityWindows
            .Select(w => new { w.Id, w.OrgId, w.ResourceId })
            .ToListAsync(cancellationToken);
        return Ok(windows);
    }

    [HttpGet("users")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public async Task<IActionResult> Users([FromServices] BookSpaceDbContext db, CancellationToken cancellationToken)
    {
        var users = await db.Users
            .Select(u => new { u.Id, u.OrgId, u.Email })
            .ToListAsync(cancellationToken);
        return Ok(users);
    }
}
