using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Bookings.CheckIn;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// WP-8 Phase 3 (docs/wp8-plan.md, decision D5): POST /bookings/{id}/check-in
// through the real pipeline. Owner-only, idempotent on repeat, no admin
// widening — the three things that make this endpoint's shape different
// from Cancel/Approve/Reject, so this file proves each one against a real
// SQL Server rather than trusting the fake repository's unit tests alone.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingCheckInEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    // Not member2@acme.test — see BookingCancelEndpointTests' own note on
    // AcmeColleague; that account is permanently deactivated by
    // AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin.
    private const string AcmeColleague = AcmeApprover;

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    public BookingCheckInEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 3, 11, hour, minute, 0, DateTimeKind.Utc);

    // ---- The happy path ----------------------------------------------------

    [Fact]
    public async Task CheckIn_LetsAMemberCheckInToTheirOwnBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var response = await client.PostAsync($"/bookings/{created.Id}/check-in", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CheckInCommandResponse>(TestJson.Options))!;

            Assert.Equal(created.Id, body.Id);
            Assert.Equal(BookingStatus.Confirmed, body.Status);
            Assert.NotEqual(default, body.CheckedInAtUtc);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND CheckedInAtUtc IS NOT NULL;",
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Idempotence (decision D5) -----------------------------------------

    // Unlike Cancel, a repeat call is 200 again with the original instant,
    // not a refusal — there is nothing here a second call could overwrite.
    [Fact]
    public async Task CheckIn_ARepeatCallIsIdempotent()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var first = await client.PostAsync($"/bookings/{created.Id}/check-in", null);
            var firstBody = (await first.Content.ReadFromJsonAsync<CheckInCommandResponse>(TestJson.Options))!;

            var second = await client.PostAsync($"/bookings/{created.Id}/check-in", null);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var secondBody = (await second.Content.ReadFromJsonAsync<CheckInCommandResponse>(TestJson.Options))!;
            Assert.Equal(firstBody.CheckedInAtUtc, secondBody.CheckedInAtUtc);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- BookingNotCheckable ------------------------------------------------

    [Fact]
    public async Task CheckIn_RefusesAPendingBookingWith409()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));
            Assert.Equal(BookingStatus.Pending, created.Status);

            var response = await client.PostAsync($"/bookings/{created.Id}/check-in", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("BookingNotCheckable", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task CheckIn_RefusesACancelledBookingWith409()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));
            (await client.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            var response = await client.PostAsync($"/bookings/{created.Id}/check-in", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("BookingNotCheckable", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Who may not check in (decision D5: owner-only, no widening) -------

    // A 404, never a 403 — the same reasoning BookingNotFoundException gives
    // for Cancel: a 403 would confirm the booking exists.
    [Fact]
    public async Task CheckIn_RefusesAnotherMembersBookingWithA404AndChangesNothing()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var owner = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(owner, resource, At(9), At(10));

            var colleague = await AuthenticatedClientAsync(AcmeColleague);
            var response = await colleague.PostAsync($"/bookings/{created.Id}/check-in", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND CheckedInAtUtc IS NULL;",
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The point of decision D5: unlike cancel, a TenantAdmin gets no
    // widening at all — this is 404, the same as any other member.
    [Fact]
    public async Task CheckIn_RefusesAnAdminCheckingInSomeoneElsesBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsync($"/bookings/{created.Id}/check-in", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Shape and authorization -------------------------------------------

    [Fact]
    public async Task CheckIn_RequiresAuthentication()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsync($"/bookings/{Guid.NewGuid()}/check-in", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CheckIn_RefusesASysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsync($"/bookings/{Guid.NewGuid()}/check-in", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Helpers -----------------------------------------------------------

    private static object Booking(Guid resourceId, DateTime start, DateTime end, int quantity = 1) =>
        new
        {
            resourceId,
            startsAtUtc = start.ToString("o"),
            endsAtUtc = end.ToString("o"),
            quantity,
            title = "Design review",
        };

    private static async Task<CreateBookingCommandResponse> CreateBookingAsync(
        HttpClient client,
        Guid resourceId,
        DateTime start,
        DateTime end,
        int quantity = 1)
    {
        var response = await client.PostAsJsonAsync(
            "/bookings", Booking(resourceId, start, end, quantity));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
            TestJson.Options))!;
    }

    private async Task<Guid> CreateBookableResourceAsync(
        int capacity = 4,
        bool requiresApproval = false,
        string admin = AcmeAdmin)
    {
        var client = await AuthenticatedClientAsync(admin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Booking CheckIn Test {Guid.NewGuid():N}",
                description = "Created by the booking check-in endpoint tests",
                resourceType = "Room",
                capacity,
                timeZoneId = "UTC",
                requiresApproval = false,
                minDurationMinutes = (int?)null,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();

        var created = (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(
            TestJson.Options))!;

        var windows = Enum.GetValues<DayOfWeek>()
            .Select(day => new { weekday = day.ToString(), opensAt = "00:00:00", closesAt = "23:59:59" })
            .ToArray();

        (await client.PutAsJsonAsync(
            $"/resources/{created.Id}/availability-windows", new { windows })).EnsureSuccessStatusCode();

        if (requiresApproval)
        {
            var approverId = await ScalarAsync<Guid>(
                "SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeApprover);

            (await client.PutAsJsonAsync(
                $"/resources/{created.Id}/approvers",
                new { approverUserIds = new[] { approverId } })).EnsureSuccessStatusCode();

            (await client.PutAsJsonAsync(
                $"/resources/{created.Id}",
                new
                {
                    name = created.Name,
                    description = "Created by the booking check-in endpoint tests",
                    resourceType = "Room",
                    capacity,
                    timeZoneId = "UTC",
                    requiresApproval = true,
                    minDurationMinutes = (int?)null,
                    maxDurationMinutes = (int?)null,
                })).EnsureSuccessStatusCode();
        }

        return created.Id;
    }

    private static async Task<string?> ReasonCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.TryGetProperty("reasonCode", out var code) ? code.GetString() : null;
    }

    private static async Task<int> CountAsync(string sql, params object[] parameters) =>
        await ScalarAsync<int>(sql, parameters);

    private static async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"@p{i}", parameters[i]);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task EnterRlsBypassAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'TenantInit',   @value = 1;
            EXEC sp_set_session_context @key = N'TenantBypass', @value = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task CleanUpAsync(Guid resourceId, string admin = AcmeAdmin)
    {
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await EnterRlsBypassAsync(connection);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM dbo.Notifications
                WHERE BookingId IN (SELECT Id FROM dbo.Bookings WHERE ResourceId = @ResourceId);

                DELETE FROM dbo.ApprovalRequests
                WHERE BookingId IN (SELECT Id FROM dbo.Bookings WHERE ResourceId = @ResourceId);

                DELETE FROM dbo.Bookings WHERE ResourceId = @ResourceId;
                """;
            command.Parameters.AddWithValue("@ResourceId", resourceId);
            await command.ExecuteNonQueryAsync();
        }

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        await context.Resources
            .IgnoreQueryFilters()
            .Where(r => r.Id == resourceId)
            .ExecuteDeleteAsync();
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
}
