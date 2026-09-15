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

// Hardening pass, item 11: POST /recurrence-rules gained request-level
// idempotency via an Idempotency-Key header, so a retry after a crash or a
// lost response resumes the same series instead of restarting it.
//
// **What only a real SQL Server can prove, that CreateRecurrenceSeriesIdempotencyTests
// (unit, against fakes) cannot**: that retrying an already-completed request
// does not insert a second Booking row for an occurrence already committed.
// That guarantee lives in BookingRepository.CreateAsync's PK-violation
// read-back (unchanged by this pass) combined with the handler now deriving
// each occurrence's booking id deterministically from (RuleId, OccurrenceDate)
// — this file is what proves the two actually compose correctly end to end.
[Collection(nameof(AuthenticationTestCollection))]
public class RecurrenceSeriesIdempotencyEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    private static readonly DateOnly StartDate = new(2027, 5, 10); // a Monday

    public RecurrenceSeriesIdempotencyEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static object Series(Guid resourceId, int occurrenceCount = 3) => new
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

    private static HttpRequestMessage PostWithKey(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/recurrence-rules")
        {
            Content = JsonContent.Create(body),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task RetryingTheSameKeyReusesTheSameRuleAndCreatesNoDuplicateBookings()
    {
        var resourceId = await CreateResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var body = Series(resourceId);

            var firstResponse = await client.SendAsync(PostWithKey(body, "retry-key-1"));
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
            var first = (await firstResponse.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(3, first.Occurrences.Count);
            Assert.All(first.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));
            Assert.Equal(3, await CountBookingsAsync(resourceId));

            // Simulates the client never seeing the first response (a
            // dropped connection, a crashed process before it could act on
            // the 201) and retrying the identical request.
            var secondResponse = await client.SendAsync(PostWithKey(body, "retry-key-1"));
            Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
            var second = (await secondResponse.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(first.RecurrenceRuleId, second.RecurrenceRuleId);
            Assert.Equal(
                first.Occurrences.Select(o => o.BookingId),
                second.Occurrences.Select(o => o.BookingId));
            Assert.All(second.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));

            // The real point: still three rows, not six.
            Assert.Equal(3, await CountBookingsAsync(resourceId));
            Assert.Equal(1, await CountRecurrenceRulesAsync(resourceId));
        }
        finally
        {
            await CleanUpAsync(resourceId);
        }
    }

    // Bug fix (found while verifying the hardening pass, not part of it):
    // resuming a series used to evaluate each occurrence's eligibility
    // against a `booked` snapshot that already included that same
    // occurrence's own prior booking — real Bookings rows the first attempt
    // committed. On a resource with no spare capacity for a second claim,
    // every occurrence was refused as SlotUnavailable, nothing was
    // (re-)Created, and the handler then tried to delete the RecurrenceRule
    // those very bookings still reference — an unhandled FK-constraint 500
    // instead of a 201 replay. Capacity 1 (exclusive) is the tightest case:
    // a single spare unit would have masked the bug entirely.
    [Fact]
    public async Task RetryingTheSameKeyOnAnExclusiveResourceStillReusesTheSameRule()
    {
        var resourceId = await CreateResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var body = Series(resourceId);

            var firstResponse = await client.SendAsync(PostWithKey(body, "retry-key-exclusive"));
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
            var first = (await firstResponse.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;
            Assert.All(first.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));

            var secondResponse = await client.SendAsync(PostWithKey(body, "retry-key-exclusive"));
            Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
            var second = (await secondResponse.Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(first.RecurrenceRuleId, second.RecurrenceRuleId);
            Assert.All(second.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));
            Assert.Equal(
                first.Occurrences.Select(o => o.BookingId),
                second.Occurrences.Select(o => o.BookingId));
            Assert.Equal(3, await CountBookingsAsync(resourceId));
            Assert.Equal(1, await CountRecurrenceRulesAsync(resourceId));
        }
        finally
        {
            await CleanUpAsync(resourceId);
        }
    }

    // Bug fix (found while verifying the hardening pass, not part of it): an
    // idempotency key is scoped to (OrgId, UserId), not to a resource, so
    // nothing stopped a key already bound to one resource's rule from being
    // resumed against a *different* request's resource — corrupting the
    // rule/resource relationship, since nothing in the schema catches a
    // RecurrenceRule whose ResourceId disagrees with its own Bookings'.
    // Reusing the key here must create an independent series on the second
    // resource rather than resuming the first resource's rule.
    [Fact]
    public async Task ReusingAKeyForADifferentResourceCreatesAnIndependentSeriesRatherThanCorrupting()
    {
        var firstResourceId = await CreateResourceAsync(capacity: 10);
        var secondResourceId = await CreateResourceAsync(capacity: 10);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var first = (await (await client.SendAsync(PostWithKey(Series(firstResourceId), "shared-key")))
                .Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(TestJson.Options))!;

            var second = (await (await client.SendAsync(PostWithKey(Series(secondResourceId), "shared-key")))
                .Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(TestJson.Options))!;

            Assert.NotEqual(first.RecurrenceRuleId, second.RecurrenceRuleId);
            Assert.Equal(3, await CountBookingsAsync(firstResourceId));
            Assert.Equal(3, await CountBookingsAsync(secondResourceId));
            Assert.Equal(1, await CountRecurrenceRulesAsync(firstResourceId));
            Assert.Equal(1, await CountRecurrenceRulesAsync(secondResourceId));
        }
        finally
        {
            await CleanUpAsync(firstResourceId);
            await CleanUpAsync(secondResourceId);
        }
    }

    [Fact]
    public async Task TwoDifferentKeysForTheSameBodyCreateTwoIndependentSeries()
    {
        var resourceId = await CreateResourceAsync(capacity: 10);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var body = Series(resourceId);

            var first = (await (await client.SendAsync(PostWithKey(body, "key-a")))
                .Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(TestJson.Options))!;
            var second = (await (await client.SendAsync(PostWithKey(body, "key-b")))
                .Content.ReadFromJsonAsync<CreateRecurrenceSeriesCommandResponse>(TestJson.Options))!;

            Assert.NotEqual(first.RecurrenceRuleId, second.RecurrenceRuleId);
            Assert.Equal(2, await CountRecurrenceRulesAsync(resourceId));
            Assert.Equal(6, await CountBookingsAsync(resourceId));
        }
        finally
        {
            await CleanUpAsync(resourceId);
        }
    }

    [Fact]
    public async Task WithNoKeyAtAllTwoIdenticalRequestsStillCreateTwoSeparateSeries()
    {
        // Documents the deliberately unchanged default: a client that never
        // supplies a key gets exactly the behaviour this codebase always
        // had, not an accidental new guarantee.
        var resourceId = await CreateResourceAsync(capacity: 10);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var body = Series(resourceId);

            (await client.SendAsync(PostWithKey(body, idempotencyKey: null))).EnsureSuccessStatusCode();
            (await client.SendAsync(PostWithKey(body, idempotencyKey: null))).EnsureSuccessStatusCode();

            Assert.Equal(2, await CountRecurrenceRulesAsync(resourceId));
            Assert.Equal(6, await CountBookingsAsync(resourceId));
        }
        finally
        {
            await CleanUpAsync(resourceId);
        }
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<Guid> CreateResourceAsync(int capacity = 4)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Idempotency Test {Guid.NewGuid():N}",
                description = "Created by RecurrenceSeriesIdempotencyEndpointTests",
                resourceType = "Room",
                capacity,
                timeZoneId = "UTC",
                requiresApproval = false,
                minDurationMinutes = (int?)null,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;

        var windows = Enum.GetValues<DayOfWeek>()
            .Select(day => new { weekday = day.ToString(), opensAt = "00:00:00", closesAt = "23:59:59" })
            .ToArray();
        (await client.PutAsJsonAsync($"/resources/{created.Id}/availability-windows", new { windows }))
            .EnsureSuccessStatusCode();

        return created.Id;
    }

    private static async Task<int> CountBookingsAsync(Guid resourceId) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE ResourceId = @p0;", resourceId);

    private static async Task<int> CountRecurrenceRulesAsync(Guid resourceId) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.RecurrenceRules WHERE ResourceId = @p0;", resourceId);

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

    private async Task CleanUpAsync(Guid resourceId)
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

                DELETE FROM dbo.RecurrenceCreationOperations
                WHERE RecurrenceRuleId IN (SELECT Id FROM dbo.RecurrenceRules WHERE ResourceId = @ResourceId);

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
