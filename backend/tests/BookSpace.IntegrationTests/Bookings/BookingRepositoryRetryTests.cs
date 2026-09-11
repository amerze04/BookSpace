using System.Net.Http.Headers;
using System.Net.Http.Json;
using BookSpace.Application.Abstractions;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// Hardening pass, P1/2 — the ambiguous-commit case: EF's execution strategy
// (CLAUDE.md §5) retries IUnitOfWork's whole delegate on a transient failure,
// and booking.Id is minted once by the caller precisely so a retry re-inserts
// the same row rather than a second one. But that guarantee has a gap of its
// own: if SQL Server actually committed the INSERT and only the *connection*
// dropped before the client learned it, the retried delegate calls
// dbo.CreateBooking again with the same Id, hits PK_Bookings, and — before
// this pass — that raw SqlException (2627) was not a retryable error EF's
// execution strategy recognises, so it propagated straight out as an
// unhandled 500 for an operation that, from the caller's perspective, had
// already succeeded.
//
// **What this proves, and at which layer.** A real dropped-connection-after-
// commit cannot be reproduced deterministically (that is the nature of the
// bug — CLAUDE.md's own header on IUnitOfWork says as much: "EF has no
// general answer to this and neither does this class; the window is the
// commit itself"). What *can* be reproduced deterministically is the
// resulting symptom: calling IBookingRepository.CreateAsync twice with the
// identical NewBooking is exactly what a retry of an already-committed insert
// looks like from BookingRepository's point of view, whatever caused the
// retry. This calls the repository directly (bypassing the handler and
// IUnitOfWork) so the two calls are back-to-back and deterministic, rather
// than trying to engineer an actual connection drop.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingRepositoryRetryTests
{
    private readonly AuthenticationTestHost _host;

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    public BookingRepositoryRetryTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 6, 7, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAsync_CalledTwiceWithTheSameId_ReportsCreatedBothTimesInsteadOfThrowing()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            await using var scope = _host.CreateScope();
            var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();

            using var bypass = TenantBypassScope.Enter();

            var booking = new NewBooking(
                Guid.NewGuid(),
                resource.Id,
                resource.MemberUserId,
                RecurrenceRuleId: null,
                At(9),
                At(10),
                Quantity: 1,
                Title: "Retried insert",
                BookingStatus.Confirmed,
                CreatedByUserId: resource.MemberUserId,
                At(0));

            var first = await bookings.CreateAsync(booking, CancellationToken.None);
            Assert.Equal(BookingCreationResult.Created, first.Result);
            Assert.Equal(BookingStatus.Confirmed, first.ActualStatus);

            // The retry: identical NewBooking, same Id, simulating
            // IUnitOfWork replaying the same delegate after the first
            // attempt's connection dropped post-commit. Must not throw.
            var second = await bookings.CreateAsync(booking, CancellationToken.None);
            Assert.Equal(BookingCreationResult.Created, second.Result);
            Assert.Equal(BookingStatus.Confirmed, second.ActualStatus);

            // Exactly one row — the "retry" did not insert a duplicate.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0;", booking.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The safety valve: a genuine, unrelated collision (a different booking's
    // details under the reused Id) must still surface as an error rather than
    // being silently reported as success.
    [Fact]
    public async Task CreateAsync_CalledTwiceWithAMismatchedSecondRequest_Throws()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            await using var scope = _host.CreateScope();
            var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();

            using var bypass = TenantBypassScope.Enter();

            var bookingId = Guid.NewGuid();

            var first = new NewBooking(
                bookingId, resource.Id, resource.MemberUserId, null, At(9), At(10), 1,
                "First", BookingStatus.Confirmed, resource.MemberUserId, At(0));
            await bookings.CreateAsync(first, CancellationToken.None);

            // Same Id, a materially different request — not a retry of the
            // same operation.
            var mismatched = new NewBooking(
                bookingId, resource.Id, resource.MemberUserId, null, At(11), At(12), 1,
                "Different interval", BookingStatus.Confirmed, resource.MemberUserId, At(0));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => bookings.CreateAsync(mismatched, CancellationToken.None));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    private sealed record TestResource(Guid Id, Guid MemberUserId);

    private async Task<TestResource> CreateResourceAsync(int capacity)
    {
        var client = await AuthenticatedClientAsync("admin@acme.test");

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Retry Test {Guid.NewGuid():N}",
                description = "Created by the booking repository retry tests",
                resourceType = "Room",
                capacity,
                timeZoneId = "UTC",
                requiresApproval = false,
                minDurationMinutes = (int?)null,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();

        var created = (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;
        var memberId = await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", "member1@acme.test");

        return new TestResource(created.Id, memberId);
    }

    private static async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@p0", parameter);

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task<int> CountAsync(string sql, params object[] parameters) => ScalarWithParamsAsync(sql, parameters);

    private static async Task<int> ScalarWithParamsAsync(string sql, object[] parameters)
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

        return (int)(await command.ExecuteScalarAsync())!;
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
            command.CommandText = "DELETE FROM dbo.Bookings WHERE ResourceId = @ResourceId;";
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

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }
}
