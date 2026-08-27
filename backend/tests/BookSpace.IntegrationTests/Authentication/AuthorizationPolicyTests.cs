using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Infrastructure.Persistence;

namespace BookSpace.IntegrationTests.Authentication;

// FR-1.4 RBAC, and the WP-2 criterion that every endpoint enforces
// authorization server-side. Exercised through the real JwtBearer handler and
// the real policies, against PolicyProbeController.
[Collection(nameof(AuthenticationTestCollection))]
public class AuthorizationPolicyTests
{
    private readonly AuthenticationTestHost _host;

    public AuthorizationPolicyTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // The seeded accounts, one per role (see SeedData).
    private const string SysAdmin = "sysadmin@bookspace.local";
    private const string TenantAdmin = "admin@acme.test";
    private const string Member = "member1@acme.test";

    [Fact]
    public async Task NoToken_ProtectedEndpoint_Returns401()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/test-probe/tenant-member");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GarbageToken_ProtectedEndpoint_Returns401()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/test-probe/tenant-member");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The point of FallbackPolicy: an endpoint nobody annotated is still closed.
    [Fact]
    public async Task UnannotatedEndpoint_WithoutAToken_IsProtectedByTheFallbackPolicy()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/test-probe/unannotated");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnannotatedEndpoint_WithAnyValidToken_IsReachable()
    {
        var client = await AuthenticatedClientAsync(Member);

        var response = await client.GetAsync("/test-probe/unannotated");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllowAnonymousEndpoint_IsReachableWithoutAToken()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/test-probe/anonymous");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The health check must stay open — the fallback policy would otherwise make
    // it useless to a load balancer.
    [Fact]
    public async Task HealthEndpoint_StaysAnonymous()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Member_TenantMemberPolicy_IsAllowed()
    {
        var client = await AuthenticatedClientAsync(Member);

        var response = await client.GetAsync("/test-probe/tenant-member");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Member_TenantAdminPolicy_IsForbidden()
    {
        var client = await AuthenticatedClientAsync(Member);

        var response = await client.GetAsync("/test-probe/tenant-admin");

        // 403, not 401: the caller is authenticated, just not permitted.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_ApproverPolicy_IsForbidden()
    {
        var client = await AuthenticatedClientAsync(Member);

        var response = await client.GetAsync("/test-probe/approver");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // TenantAdmin satisfies the Approver policy without being granted the
    // Approver role — the reason these are policies rather than role attributes.
    [Fact]
    public async Task TenantAdmin_ApproverPolicy_IsAllowed()
    {
        var client = await AuthenticatedClientAsync(TenantAdmin);

        var response = await client.GetAsync("/test-probe/approver");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_SysAdminPolicy_IsForbidden()
    {
        var client = await AuthenticatedClientAsync(TenantAdmin);

        var response = await client.GetAsync("/test-probe/sysadmin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SysAdmin_SysAdminPolicy_IsAllowed()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.GetAsync("/test-probe/sysadmin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // SysAdmin folds into the admin-level policies...
    [Fact]
    public async Task SysAdmin_TenantAdminPolicy_IsAllowed()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.GetAsync("/test-probe/tenant-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ...but deliberately NOT into tenant-member scope. PRD §2: the Platform
    // Operator must never see tenant booking content in routine operation, so
    // SysAdmin is a separate axis rather than the top of a ladder. Having no
    // orgId claim is what expresses that.
    [Fact]
    public async Task SysAdmin_TenantMemberPolicy_IsForbidden()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.GetAsync("/test-probe/tenant-member");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Proves the claim shape survives issuing, signing, and validation — not
    // just that the token service builds it (decision 0009).
    [Fact]
    public async Task TenantUserToken_CarriesOrgIdAndRoleClaimsThroughValidation()
    {
        var client = await AuthenticatedClientAsync(TenantAdmin);

        var claims = await client.GetFromJsonAsync<List<ClaimDto>>("/test-probe/claims");

        Assert.Contains(claims!, c => c.Type == "orgId");
        Assert.Contains(claims!, c => c.Type == "sub");
        Assert.Contains(claims!, c => c.Type == "email");
        Assert.Contains(claims!, c => c.Value == "TenantAdmin");
    }

    [Fact]
    public async Task SysAdminToken_CarriesNoOrgIdClaim()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var claims = await client.GetFromJsonAsync<List<ClaimDto>>("/test-probe/claims");

        Assert.DoesNotContain(claims!, c => c.Type == "orgId");
        Assert.Contains(claims!, c => c.Value == "SysAdmin");
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = SeedData.SeedPassword });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private sealed record ClaimDto(string Type, string Value);
}
