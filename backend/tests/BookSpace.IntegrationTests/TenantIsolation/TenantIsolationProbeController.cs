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
