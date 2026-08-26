using BookSpace.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.IntegrationTests.Authentication;

// Test-only. The API has no business endpoints yet, so there is nothing real for
// the authorization policies to guard — this gives each policy (and the
// secure-by-default fallback) something to be asserted against. It lives in the
// test project deliberately: it must never ship in BookSpace.Api.
//
// Registered by AuthenticationTestHost adding this assembly as an application part.
[ApiController]
[Route("test-probe")]
public sealed class PolicyProbeController : ControllerBase
{
    // No [Authorize] and no [AllowAnonymous]: this is the one that proves the
    // fallback policy protects endpoints nobody remembered to annotate.
    [HttpGet("unannotated")]
    public IActionResult Unannotated() => Ok("reached");

    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok("reached");

    [HttpGet("sysadmin")]
    [Authorize(Policy = AuthorizationPolicies.SysAdminOnly)]
    public IActionResult SysAdminOnly() => Ok("reached");

    [HttpGet("tenant-admin")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    public IActionResult TenantAdmin() => Ok("reached");

    [HttpGet("approver")]
    [Authorize(Policy = AuthorizationPolicies.Approver)]
    public IActionResult Approver() => Ok("reached");

    [HttpGet("tenant-member")]
    [Authorize(Policy = AuthorizationPolicies.TenantMember)]
    public IActionResult TenantMember() => Ok("reached");

    // Echoes back what the JwtBearer handler actually put on the principal, so a
    // test can assert the claim shape survives a real round trip rather than
    // just inspecting a locally-minted token.
    [HttpGet("claims")]
    [Authorize]
    public IActionResult Claims() =>
        Ok(User.Claims.Select(c => new { type = c.Type, value = c.Value }));
}
