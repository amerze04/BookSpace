using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.TenantIsolation;

// AC-4: "Given two tenants with identically named resources, when a member of
// tenant A requests bookings, then no tenant B data is ever returned,
// regardless of identifiers supplied." CLAUDE.md §4.2's three mechanisms are
// exercised at two independent levels here: through the real HTTP pipeline
// (query filter + RLS together, exactly as production traffic would hit
// them), and via a raw ADO.NET connection with no EF involved at all (RLS
// alone, proving it doesn't depend on the application behaving correctly).
[Collection(nameof(AuthenticationTestCollection))]
public class TenantIsolationTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeMember = "member1@acme.test";
    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public TenantIsolationTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task ResourceById_WithOtherTenantsRealId_ReturnsNotFound()
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/test-probe-tenant/resources/{globexResourceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResourceList_NeverContainsOtherTenantRows()
    {
        var acmeOrgId = await GetOrgIdAsync("acme");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var resources = await client.GetFromJsonAsync<List<ResourceDto>>("/test-probe-tenant/resources");

        Assert.Equal(2, resources!.Count);
        Assert.All(resources, r => Assert.Equal(acmeOrgId, r.OrgId));
    }

    [Fact]
    public async Task UserList_NeverContainsOtherTenantOrSysAdminRows()
    {
        var acmeOrgId = await GetOrgIdAsync("acme");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var users = await client.GetFromJsonAsync<List<UserDto>>("/test-probe-tenant/users");

        Assert.Equal(4, users!.Count);
        Assert.All(users, u => Assert.Equal(acmeOrgId, u.OrgId));
    }

    // WP-3 decision D1: before this step neither child table had an OrgId, a
    // query filter, or an RLS predicate, so this probe — which filters by Id
    // alone — would have handed Acme's member a Globex blackout.
    [Fact]
    public async Task BlackoutById_WithOtherTenantsRealId_ReturnsNotFound()
    {
        var globexBlackoutId = await GetAnyBlackoutIdAsync("globex");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/test-probe-tenant/blackouts/{globexBlackoutId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BlackoutById_WithOwnTenantsId_IsStillReachable()
    {
        var acmeBlackoutId = await GetAnyBlackoutIdAsync("acme");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/test-probe-tenant/blackouts/{acmeBlackoutId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AvailabilityWindowList_NeverContainsOtherTenantRows()
    {
        var acmeOrgId = await GetOrgIdAsync("acme");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var windows = await client.GetFromJsonAsync<List<AvailabilityWindowDto>>(
            "/test-probe-tenant/availability-windows");

        // Seed: 2 Acme resources x 5 weekday windows. The other 10 belong to Globex.
        Assert.Equal(10, windows!.Count);
        Assert.All(windows, w => Assert.Equal(acmeOrgId, w.OrgId));
    }

    // ---- Bookings (WP-4 Phase 3) -------------------------------------------
    //
    // **This group used to be a single smoke check**, from WP-2 until
    // 2026-09-08: no Bookings existed to leak, because dbo.CreateBooking did not
    // exist and CLAUDE.md §4.1 admits no other way to write one, so all that
    // could be asserted was that the filter clause built without throwing. The
    // seed writes six bookings now, so the same questions the other five tables
    // answer here can finally be asked of Bookings — which closes AC-4's last
    // gap.
    //
    // Note that a *cross-tenant read through the real endpoints* was already
    // covered by BookingReadEndpointTests and BookingCancelEndpointTests. What
    // was missing, and is here, is the database half: the leak a handler that
    // forgot its OrgId would produce, and the rows a raw connection sees.

    [Fact]
    public async Task BookingById_WithOtherTenantsRealId_ReturnsNotFound()
    {
        var globexBookingId = await GetAnyBookingIdAsync("globex");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/test-probe-tenant/bookings/{globexBookingId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The other half of the pair, and the reason the test above means anything:
    // an id of the same shape from the caller's own tenant is reachable through
    // the identical probe, so "not found" is isolation rather than a broken
    // route.
    [Fact]
    public async Task BookingById_WithOwnTenantsId_IsStillReachable()
    {
        var acmeBookingId = await GetAnyBookingIdAsync("acme");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/test-probe-tenant/bookings/{acmeBookingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BookingList_NeverContainsOtherTenantRows()
    {
        var acmeOrgId = await GetOrgIdAsync("acme");
        var client = await AuthenticatedClientAsync(AcmeMember);

        var bookings = await client.GetFromJsonAsync<List<BookingDto>>("/test-probe-tenant/bookings");

        // Seed: 3 bookings per tenant. The other 3 belong to Globex. Asserted as
        // an exact count rather than "all mine", so a filter that accidentally
        // matched nothing would fail here too.
        Assert.Equal(3, bookings!.Count);
        Assert.All(bookings, b => Assert.Equal(acmeOrgId, b.OrgId));
    }

    // The EF half on its own: no tenant context at all, so the query filter
    // compares OrgId against null and matches nothing. Rows plainly exist now —
    // this is the test that was a smoke check while the table was empty.
    [Fact]
    public async Task BookingsQueryFilter_WithNoTenantContext_ReturnsEmpty()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var bookings = await context.Bookings.ToListAsync();

        Assert.Empty(bookings);

        // And they are there to be missed, which is what makes the assertion
        // above evidence rather than a tautology.
        using var _ = TenantBypassScope.Enter();
        Assert.Equal(6, await context.Bookings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task RawConnection_WithNoSessionContext_SeesZeroResourceRows()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var count = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Resources;");

        Assert.Equal(0, count);
    }

    // The DB half of D1: no EF, no query filter — just the two new RLS
    // predicates. Rows physically exist (the bypass-scoped helpers above read
    // them), yet an uninitialized connection sees none.
    [Fact]
    public async Task RawConnection_WithNoSessionContext_SeesZeroChildTableRows()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.AvailabilityWindows;"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.BlackoutPeriods;"));
        // Decision 0025: RecurrenceRules joined the tenant-filtered tables in
        // WP-5 Phase 2, same RLS predicate as the two above.
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.RecurrenceRules;"));
    }

    // Bookings, with no EF anywhere: the rows exist and an uninitialized
    // connection sees none of them. This is also the failure mode decision 0023
    // calls fail-open — a connection in exactly this state would count zero
    // overlapping bookings inside dbo.CreateBooking and overbook, which is why
    // the procedure reads Resources first and refuses.
    [Fact]
    public async Task RawConnection_WithNoSessionContext_SeesZeroBookingRows()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Bookings;"));
    }

    // Regression test for the TenantInit gap specifically: a predicate that
    // only checked "OrgId matches, or both are NULL" would leak SysAdmin
    // Users rows (OrgId IS NULL) to a connection that set OrgId but never
    // marked itself initialized.
    [Fact]
    public async Task RawConnection_WithOrgIdButNoTenantInit_StillSeesZeroUserRows()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "EXEC sys.sp_set_session_context @key = N'OrgId', @value = NULL;");

        var count = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Users;");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RawConnection_WithFullSessionContext_SeesOnlyItsTenant()
    {
        var acmeOrgId = await GetOrgIdAsync("acme");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "EXEC sys.sp_set_session_context @key = N'TenantInit', @value = 1;");
        await ExecuteAsync(connection, "EXEC sys.sp_set_session_context @key = N'TenantBypass', @value = 0;");
        await ExecuteAsync(connection, $"EXEC sys.sp_set_session_context @key = N'OrgId', @value = '{acmeOrgId}';");

        var resourceCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Resources;");
        var userCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Users;");
        var windowCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.AvailabilityWindows;");
        var blackoutCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.BlackoutPeriods;");
        var bookingCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Bookings;");
        var recurrenceRuleCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.RecurrenceRules;");

        Assert.Equal(2, resourceCount);
        Assert.Equal(4, userCount);
        // Acme's half of the seeded 20 windows and 2 blackouts.
        Assert.Equal(10, windowCount);
        Assert.Equal(1, blackoutCount);
        // And of the seeded 6 bookings (WP-4 Phase 3).
        Assert.Equal(3, bookingCount);
        // Acme's one seeded weekly standup (decision 0025).
        Assert.Equal(1, recurrenceRuleCount);
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

    // Bypass-scoped direct DbContext access, same justification as
    // SeedDataTests: test setup needs to see across tenants to know what real
    // IDs to probe with, independent of what the isolation layer under test
    // allows a normal request to see.
    private async Task<Guid> GetOrgIdAsync(string slug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var org = await context.Organizations.SingleAsync(o => o.Slug == slug);
        return org.Id;
    }

    private async Task<Guid> GetAnyResourceIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        // Bypasses both layers: the EF filter (IgnoreQueryFilters) and RLS
        // (TenantBypassScope, read by TenantSessionContextInterceptor) — this
        // scope has no HttpContext, so ICurrentTenant.OrgId is null and RLS
        // would otherwise hide every Resources row regardless.
        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources.IgnoreQueryFilters().FirstAsync(r => r.OrgId == orgId);
        return resource.Id;
    }

    private async Task<Guid> GetAnyBlackoutIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        // Both layers bypassed, same reasoning as GetAnyResourceIdAsync — which
        // now applies to BlackoutPeriods too, since D1 brought it under the
        // query filter and RLS.
        using var _ = TenantBypassScope.Enter();
        var blackout = await context.BlackoutPeriods.IgnoreQueryFilters().FirstAsync(b => b.OrgId == orgId);
        return blackout.Id;
    }

    // Both layers bypassed, same reasoning as GetAnyResourceIdAsync — which now
    // applies to Bookings too, since the seed finally writes some (WP-4 Phase 3).
    private async Task<Guid> GetAnyBookingIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        using var _ = TenantBypassScope.Enter();
        var booking = await context.Bookings.IgnoreQueryFilters().FirstAsync(b => b.OrgId == orgId);
        return booking.Id;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed record ResourceDto(Guid Id, Guid OrgId, string Name);
    private sealed record UserDto(Guid Id, Guid? OrgId, string Email);
    private sealed record AvailabilityWindowDto(Guid Id, Guid OrgId, Guid ResourceId);
    private sealed record BookingDto(Guid Id, Guid OrgId, Guid ResourceId, Guid UserId);
}
