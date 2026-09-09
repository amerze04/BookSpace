using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.RecurrenceRules;
using BookSpace.Application.Features.RecurrenceRules.CreateSeries;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.RecurrenceRules;

// WP-5 Phase 1b: POST /recurrence-rules through the real pipeline — real
// middleware, real JwtBearer handler, real authorization policies, real SQL
// Server, and a real dbo.CreateBooking call per occurrence.
//
// This file proves FR-5.1 (create) and FR-5.4 (never drop a conflicting
// occurrence silently): a member creates a recurring series, every occurrence
// is reported, and a series that could reserve nothing at all is a single 422
// rather than a 201 with an empty list.
//
// Not here: per-occurrence view/cancel (Phase 2), approvals (Phase 3), and the
// DST mechanics (RecurrenceExpansionTests already proves those against real
// tzdata) — resources here stay in UTC for the same reason
// CreateBookingEndpointTests gives.
[Collection(nameof(AuthenticationTestCollection))]
public class CreateRecurrenceSeriesEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public CreateRecurrenceSeriesEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // A fixed future Monday, comfortably clear of any seeded data (which
    // anchors to "the next weekday" from whenever the suite actually runs)
    // and of any DST transition, since the resource stays in UTC.
    private static readonly DateOnly StartDate = new(2027, 3, 8);

    private static object Series(
        Guid resourceId,
        int occurrenceCount = 3,
        string localStartTime = "09:00:00",
        string localEndTime = "10:00:00",
        DateOnly? startDate = null,
        int quantity = 1) =>
        new
        {
            resourceId,
            frequency = "Weekly",
            intervalValue = 1,
            localStartTime,
            localEndTime,
            startDate = (startDate ?? StartDate).ToString("yyyy-MM-dd"),
            occurrenceCount,
            quantity,
            title = "Standup",
        };

    // ---- The happy path (FR-5.1) --------------------------------------------

    [Fact]
    public async Task Post_CreatesOneConfirmedBookingPerOccurrence()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resource, occurrenceCount: 3));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.NotEqual(Guid.Empty, body.RecurrenceRuleId);
            Assert.Equal(3, body.Occurrences.Count);
            Assert.All(
                body.Occurrences,
                o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));
            Assert.Equal(
                new[] { StartDate, StartDate.AddDays(7), StartDate.AddDays(14) },
                body.Occurrences.Select(o => o.OccurrenceDate));
            Assert.Equal(3, body.Occurrences.Select(o => o.BookingId).Distinct().Count());

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.RecurrenceRules WHERE Id = @p0;", body.RecurrenceRuleId));
            Assert.Equal(3, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Bookings
                WHERE RecurrenceRuleId = @p0 AND Status = 'Confirmed';
                """,
                body.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_EnqueuesAConfirmationPerOccurrence()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resource, occurrenceCount: 2));
            var body = (await response.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(2, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Notifications
                WHERE BookingId IN (
                    SELECT Id FROM dbo.Bookings WHERE RecurrenceRuleId = @p0
                ) AND Kind = 'Confirmed';
                """,
                body.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- FR-7.1: approval routing, per occurrence ---------------------------

    [Fact]
    public async Task Post_OnAnApprovalGatedResource_PendsEveryOccurrence()
    {
        var resource = await CreateResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resource, occurrenceCount: 2));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.All(body.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));

            Assert.Equal(2, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Bookings
                WHERE RecurrenceRuleId = @p0 AND Status = 'Pending';
                """,
                body.RecurrenceRuleId));
            Assert.Equal(2, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.ApprovalRequests
                WHERE BookingId IN (SELECT Id FROM dbo.Bookings WHERE RecurrenceRuleId = @p0)
                AND Decision = 'Pending';
                """,
                body.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- FR-5.4: a conflict surfaced, never dropped -------------------------

    [Fact]
    public async Task Post_ASeriesHittingABlackoutMidRun_RefusesOnlyThatOccurrenceAndCreatesTheRest()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var admin = await AuthenticatedClientAsync(AcmeAdmin);

            // Covers only the second occurrence's exact interval.
            var blackoutStart = StartDate.AddDays(7).ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
            var blackoutEnd = StartDate.AddDays(7).ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc);
            (await admin.PostAsJsonAsync(
                $"/resources/{resource}/blackout-periods",
                new
                {
                    startsAtUtc = blackoutStart.ToString("o"),
                    endsAtUtc = blackoutEnd.ToString("o"),
                    reason = "Room flooded",
                })).EnsureSuccessStatusCode();

            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resource, occurrenceCount: 3));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(RecurrenceOccurrenceReportStatus.Created, body.Occurrences[0].Status);
            Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, body.Occurrences[1].Status);
            Assert.Equal("BlackoutPeriod", body.Occurrences[1].ReasonCode);
            Assert.Equal(RecurrenceOccurrenceReportStatus.Created, body.Occurrences[2].Status);

            // Never dropped silently — a row for every occurrence, not just the
            // ones that succeeded.
            Assert.Equal(3, body.Occurrences.Count);

            Assert.Equal(2, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE RecurrenceRuleId = @p0;", body.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- The all-refused case: 422, not a 201 with an empty list ------------

    [Fact]
    public async Task Post_WhenEveryOccurrenceIsOutsideAvailability_Refuses422WithTheFullBreakdown()
    {
        // No availability windows at all: every occurrence is refused.
        var resource = await CreateResourceAsync(setAvailabilityWindows: false);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resource, occurrenceCount: 3));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("NoOccurrencesCreated", body.GetProperty("reasonCode").GetString());

            var occurrences = body.GetProperty("occurrences").EnumerateArray().ToList();
            Assert.Equal(3, occurrences.Count);
            Assert.All(
                occurrences,
                o => Assert.Equal("Refused", o.GetProperty("status").GetString()));
            Assert.All(
                occurrences,
                o => Assert.Equal("OutsideAvailability", o.GetProperty("reasonCode").GetString()));

            // No Booking rows at all — nothing was reserved.
            Assert.Equal(0, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Bookings b
                JOIN dbo.RecurrenceRules r ON r.Id = b.RecurrenceRuleId
                WHERE r.ResourceId = @p0;
                """,
                resource));

            // Nor does the series shell itself survive — a 422 leaves no
            // trace at all, not even the RecurrenceRule row created before
            // any occurrence was attempted.
            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM dbo.RecurrenceRules WHERE ResourceId = @p0;", resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Structured errors, the same table CreateBookingEndpointTests keeps --

    [Fact]
    public async Task Post_RefusesAnUnknownResourceWith404()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.PostAsJsonAsync("/recurrence-rules", Series(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ResourceNotFound", await ReasonCodeAsync(response));
    }

    [Fact]
    public async Task Post_RefusesAMalformedIntervalValueWith400()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync(
                "/recurrence-rules",
                new
                {
                    resourceId = resource,
                    frequency = "Weekly",
                    intervalValue = 0,
                    localStartTime = "09:00:00",
                    localEndTime = "10:00:00",
                    startDate = StartDate.ToString("yyyy-MM-dd"),
                    occurrenceCount = 3,
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Authorization -------------------------------------------------------

    [Fact]
    public async Task Post_IsRefusedWithoutAToken()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/recurrence-rules", Series(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_IsForbiddenToASysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsJsonAsync("/recurrence-rules", Series(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A member action, not an admin write — every tenant role may create their
    // own recurring series, the same contrast CreateBookingEndpointTests draws
    // for one-off bookings.
    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    [InlineData(AcmeAdmin)]
    public async Task Post_IsAllowedToEveryTenantRole(string email)
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(email);
            var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resource, occurrenceCount: 1));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private async Task<Guid> CreateResourceAsync(
        int capacity = 4,
        bool requiresApproval = false,
        bool setAvailabilityWindows = true,
        string admin = AcmeAdmin)
    {
        var client = await AuthenticatedClientAsync(admin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Recurrence Test {Guid.NewGuid():N}",
                description = "Created by the recurrence-rule endpoint tests",
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

        if (setAvailabilityWindows)
        {
            // Open every weekday, all day but for the last second (decision
            // 0022), so nothing in this file has to reason about closing times.
            var windows = Enum.GetValues<DayOfWeek>()
                .Select(day => new { weekday = day.ToString(), opensAt = "00:00:00", closesAt = "23:59:59" })
                .ToArray();

            (await client.PutAsJsonAsync(
                $"/resources/{created.Id}/availability-windows", new { windows })).EnsureSuccessStatusCode();
        }

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
                    description = "Created by the recurrence-rule endpoint tests",
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

    // RecurrenceRules and Bookings both reference Resources with NoAction
    // (CLAUDE.md §4.5), and Notifications/ApprovalRequests reference Bookings —
    // so the order matters, same shape as CreateBookingEndpointTests' cleanup,
    // with RecurrenceRules added at the end.
    private async Task CleanUpAsync(Guid resourceId, string admin = AcmeAdmin)
    {
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await EnterRlsBypassAsync(connection);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM dbo.Notifications
                WHERE BookingId IN (SELECT Id FROM dbo.Bookings WHERE ResourceId = @ResourceId)
                   OR RecurrenceRuleId IN (SELECT Id FROM dbo.RecurrenceRules WHERE ResourceId = @ResourceId);

                DELETE FROM dbo.ApprovalRequests
                WHERE BookingId IN (SELECT Id FROM dbo.Bookings WHERE ResourceId = @ResourceId);

                DELETE FROM dbo.Bookings WHERE ResourceId = @ResourceId;

                DELETE FROM dbo.RecurrenceRules WHERE ResourceId = @ResourceId;
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
