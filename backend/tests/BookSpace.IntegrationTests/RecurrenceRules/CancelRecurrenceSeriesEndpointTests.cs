using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.RecurrenceRules.CancelSeries;
using BookSpace.Application.Features.RecurrenceRules.CreateSeries;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.RecurrenceRules;

// WP-5 Phase 2: POST /recurrence-rules/{id}/cancel through the real pipeline.
//
// This file proves FR-5.3 ("cancel the whole remaining series") and is also
// where decision 0025 gets its real proof — before it, this endpoint's query
// would have had no tenant restriction at all for a TenantAdmin's reach, so
// Cancel_RefusesAnAdminReachingIntoAnotherTenant below is the test that would
// have caught the gap this package found and fixed.
//
// Resources stay in UTC, same reasoning as CreateRecurrenceSeriesEndpointTests.
[Collection(nameof(AuthenticationTestCollection))]
public class CancelRecurrenceSeriesEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string GlobexMember = "member1@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public CancelRecurrenceSeriesEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static readonly DateOnly StartDate = new(2027, 3, 8);

    private static object Series(Guid resourceId, int occurrenceCount = 3) =>
        new
        {
            resourceId,
            frequency = "Weekly",
            intervalValue = 1,
            localStartTime = "09:00:00",
            localEndTime = "10:00:00",
            startDate = StartDate.ToString("yyyy-MM-dd"),
            occurrenceCount,
            quantity = 1,
            title = "Standup",
        };

    private static async Task<CreateRecurrenceSeriesCommandResponse> CreateSeriesAsync(
        HttpClient client, Guid resourceId, int occurrenceCount = 3)
    {
        var response = await client.PostAsJsonAsync("/recurrence-rules", Series(resourceId, occurrenceCount));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
            TestJson.Options))!;
    }

    // ---- The happy path (FR-5.3) ---------------------------------------------

    [Fact]
    public async Task Cancel_LetsTheOwnerCancelTheirOwnSeriesAndEveryOccurrence()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(client, resource);

            var response = await client.PostAsJsonAsync(
                $"/recurrence-rules/{series.RecurrenceRuleId}/cancel", new { reason = "Standup discontinued" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CancelRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(series.RecurrenceRuleId, body.RecurrenceRuleId);
            Assert.Equal(3, body.CancelledBookingIds.Count);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.RecurrenceRules WHERE Id = @p0 AND Status = 'Cancelled';",
                series.RecurrenceRuleId));
            Assert.Equal(3, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Bookings
                WHERE RecurrenceRuleId = @p0 AND Status = 'Cancelled' AND CancellationReason = @p1;
                """,
                series.RecurrenceRuleId,
                "Standup discontinued"));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // A occurrence already cancelled individually before the series cancel is
    // left exactly as it was — not re-touched, and its own actor/reason
    // survive. Only the two still-live occurrences are counted.
    [Fact]
    public async Task Cancel_LeavesAnAlreadyCancelledOccurrenceUntouched()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(client, resource, occurrenceCount: 3);
            var firstOccurrenceId = series.Occurrences[0].BookingId!.Value;

            (await client.PostAsJsonAsync($"/bookings/{firstOccurrenceId}/cancel", new { reason = "Individually cancelled" }))
                .EnsureSuccessStatusCode();

            var response = await client.PostAsync($"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CancelRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            // Only the two occurrences the cascade actually touched.
            Assert.Equal(2, body.CancelledBookingIds.Count);
            Assert.DoesNotContain(firstOccurrenceId, body.CancelledBookingIds);

            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Bookings
                WHERE Id = @p0 AND CancellationReason = 'Individually cancelled';
                """,
                firstOccurrenceId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The freed slot is bookable again — the exclusive-resource form of the
    // assertion, so the new booking could only succeed if the occurrence
    // genuinely stopped holding its unit.
    [Fact]
    public async Task Cancel_FreesEachOccurrencesSlot()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(client, resource, occurrenceCount: 1);
            var occurrenceDate = series.Occurrences[0].OccurrenceDate;

            (await client.PostAsync($"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null))
                .EnsureSuccessStatusCode();

            var start = occurrenceDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
            var end = occurrenceDate.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);

            var colleague = await AuthenticatedClientAsync(AcmeApprover);
            var rebooked = await colleague.PostAsJsonAsync(
                "/bookings",
                new { resourceId = resource, startsAtUtc = start.ToString("o"), endsAtUtc = end.ToString("o"), quantity = 1 });

            Assert.Equal(HttpStatusCode.Created, rebooked.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- The notification asymmetry ------------------------------------------

    [Fact]
    public async Task Cancel_ByTheOwnerEnqueuesNoNotification()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(client, resource);

            (await client.PostAsync($"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null))
                .EnsureSuccessStatusCode();

            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Notifications WHERE RecurrenceRuleId = @p0 AND Kind = 'SeriesCancelled';",
                series.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // One summary row, not one per occurrence (owner's answer, 2026-09-08) —
    // asserted with a three-occurrence series so the distinction is real.
    [Fact]
    public async Task Cancel_ByAnAdminEnqueuesExactlyOneSummaryNotificationForTheOwner()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(member, resource, occurrenceCount: 3);

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null))
                .EnsureSuccessStatusCode();

            var memberId = await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeMember);

            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Notifications
                WHERE RecurrenceRuleId = @p0 AND Kind = 'SeriesCancelled' AND RecipientUserId = @p1
                  AND BookingId IS NULL AND OccurrenceDate IS NULL;
                """,
                series.RecurrenceRuleId,
                memberId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- AC-4: tenant isolation, the proof decision 0025 exists for ----------

    [Fact]
    public async Task Cancel_IsByteIdenticalForEveryUnreachableSeries()
    {
        var acmeResource = await CreateResourceAsync();
        var globexResource = await CreateResourceAsync(admin: GlobexAdmin);

        try
        {
            var acmeMember = await AuthenticatedClientAsync(AcmeMember);
            var globexSeries = await CreateSeriesAsync(
                await AuthenticatedClientAsync(GlobexMember), globexResource);

            var otherTenant = await acmeMember.PostAsync(
                $"/recurrence-rules/{globexSeries.RecurrenceRuleId}/cancel", null);
            var nonexistent = await acmeMember.PostAsync($"/recurrence-rules/{Guid.NewGuid()}/cancel", null);

            Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
            Assert.Equal(await ComparableBodyAsync(nonexistent), await ComparableBodyAsync(otherTenant));
        }
        finally
        {
            await CleanUpAsync(acmeResource);
            await CleanUpAsync(globexResource, GlobexAdmin);
        }
    }

    // The gap decision 0025 closed, proved directly: before it, a TenantAdmin's
    // dropped owner filter (BookingOwnerFilter.AnyOwner) had no tenant
    // restriction under it at all for RecurrenceRules, so this would have
    // succeeded. It must not.
    [Fact]
    public async Task Cancel_RefusesAnAdminReachingIntoAnotherTenant()
    {
        var globexResource = await CreateResourceAsync(admin: GlobexAdmin);

        try
        {
            var globexSeries = await CreateSeriesAsync(
                await AuthenticatedClientAsync(GlobexMember), globexResource);

            var acmeAdmin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await acmeAdmin.PostAsync(
                $"/recurrence-rules/{globexSeries.RecurrenceRuleId}/cancel", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.RecurrenceRules WHERE Id = @p0 AND Status = 'Active';",
                globexSeries.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(globexResource, GlobexAdmin);
        }
    }

    [Fact]
    public async Task Cancel_RefusesAnotherMembersSeriesWithA404AndChangesNothing()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var owner = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(owner, resource);

            var colleague = await AuthenticatedClientAsync(AcmeApprover);
            var response = await colleague.PostAsync(
                $"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("RecurrenceRuleNotFound", await ReasonCodeAsync(response));

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.RecurrenceRules WHERE Id = @p0 AND Status = 'Active';",
                series.RecurrenceRuleId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- RecurrenceRuleNotCancellable -----------------------------------------

    [Fact]
    public async Task Cancel_RefusesASecondCancellation()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(client, resource);

            (await client.PostAsync($"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null))
                .EnsureSuccessStatusCode();

            var second = await client.PostAsync($"/recurrence-rules/{series.RecurrenceRuleId}/cancel", null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
            Assert.Equal("RecurrenceRuleNotCancellable", await ReasonCodeAsync(second));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Authorization ---------------------------------------------------------

    [Fact]
    public async Task Cancel_RequiresAuthentication()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsync($"/recurrence-rules/{Guid.NewGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_RefusesASysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsync($"/recurrence-rules/{Guid.NewGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_RefusesAnOverLongReason()
    {
        var resource = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var series = await CreateSeriesAsync(client, resource);

            var response = await client.PostAsJsonAsync(
                $"/recurrence-rules/{series.RecurrenceRuleId}/cancel", new { reason = new string('x', 301) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private async Task<Guid> CreateResourceAsync(int capacity = 4, string admin = AcmeAdmin)
    {
        var client = await AuthenticatedClientAsync(admin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Series Cancel Test {Guid.NewGuid():N}",
                description = "Created by the recurrence-rule cancel endpoint tests",
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

        return created.Id;
    }

    private static async Task<string?> ReasonCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.TryGetProperty("reasonCode", out var code) ? code.GetString() : null;
    }

    private static async Task<string> ComparableBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var fields = body.EnumerateObject()
            .Where(p => p.Name is not ("correlationId" or "traceId"))
            .Select(p => $"{p.Name}={p.Value.GetRawText()}")
            .OrderBy(text => text, StringComparer.Ordinal);

        return string.Join("|", fields);
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
