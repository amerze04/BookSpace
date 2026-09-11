using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// Hardening pass, P0: a booking racing a blackout over the same resource and
// interval — the case decision 0001's absolute-priority guarantee names and
// CreateBookingProcedureTests / BookingConcurrencyEndpointTests never cover,
// because both of those race one command type against itself.
//
// **The bug this proves fixed.** Before this pass, CreateBlackoutPeriodCommand-
// RequestHandler and UpdateBlackoutPeriodCommandRequestHandler read "which
// bookings does this cancel" with a plain, unlocked LINQ query, before any
// transaction existed. A booking committed by dbo.CreateBooking in the gap
// between that read and the blackout's own SaveChangesAsync was never
// selected for cancellation — a live Pending/Confirmed booking left inside a
// blackout, forever. IBlackoutPeriodRepository.LockBookingRangeAsync closes it
// by taking the identical UPDLOCK, HOLDLOCK range lock over Bookings that
// dbo.CreateBooking/dbo.ApproveBooking already take, before the read.
//
// **The invariant under test is a database fact, not an HTTP outcome**: after
// the race, no Bookings row for this resource may be Pending or Confirmed and
// overlap a BlackoutPeriods row for the same resource. Both orderings are
// individually valid — a booking that wins the race and commits first must be
// found and cancelled by the blackout; a blackout that wins must be seen by
// the booking's own re-check and refuse it — so the test does not assert which
// one happened, only that the invariant holds either way, exactly the two
// outcomes the hardening request asked for.
//
// **The gate is a setup cost, not a timing assumption**, matching
// BookingConcurrencyEndpointTests: both requests are fully prepared (logged
// in, payload built) before a TaskCompletionSource releases them together, so
// the race is over the database lock and not over which HTTP request happened
// to leave the client first. Run across several trials with a fresh resource
// each time, because which side wins a given race is not deterministic and
// the invariant has to hold under both orderings, not just the one a single
// trial happens to produce.
[Collection(nameof(AuthenticationTestCollection))]
public class BlackoutBookingConcurrencyEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    private static readonly TimeSpan GateDelay = TimeSpan.FromMilliseconds(200);

    public BlackoutBookingConcurrencyEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // Its own day, distinct from the sibling concurrency files', so a stray row
    // from a failed cleanup elsewhere cannot silently join this race.
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 5, 3, hour, minute, 0, DateTimeKind.Utc);

    // A booking racing a *create* blackout over the identical interval, on an
    // exclusive resource so there is exactly one unit for the two operations to
    // contend over. Run across several trials: which side wins is genuinely
    // non-deterministic (both are legitimate SQL Server outcomes of the same
    // deadlock-and-retry mechanism decision 0023 already documents), so a
    // single race could pass by only ever exercising one ordering.
    [Fact]
    public async Task BookingRacingBlackoutCreate_NeverLeavesALiveBookingInsideTheBlackout()
    {
        for (var trial = 0; trial < 8; trial++)
        {
            var resource = await CreateBookableResourceAsync(capacity: 1);

            try
            {
                var member = await AuthenticatedClientAsync(AcmeMember);
                var admin = await AuthenticatedClientAsync(AcmeAdmin);

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var booking = BookAsync();
                var blackout = BlackOutAsync();

                await Task.Delay(GateDelay);
                ready.SetResult();

                var bookingResponse = await booking;
                var blackoutResponse = await blackout;

                Assert.True(
                    bookingResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict
                        or HttpStatusCode.UnprocessableEntity,
                    $"Trial {trial}: the booking answered {(int)bookingResponse.StatusCode}; only 201, 409 "
                    + "or 422 is correct. A 500 here is a deadlock that escaped the 1205 retry.");

                Assert.Equal(HttpStatusCode.Created, blackoutResponse.StatusCode);

                // **The invariant.** Whichever side won, no live claim may
                // remain inside the blackout's interval on this resource.
                Assert.Equal(0, await LiveBookingsInsideAnyBlackoutAsync(resource));

                async Task<HttpResponseMessage> BookAsync()
                {
                    await ready.Task;

                    return await member.PostAsJsonAsync(
                        "/bookings",
                        new
                        {
                            resourceId = resource,
                            startsAtUtc = At(9).ToString("o"),
                            endsAtUtc = At(10).ToString("o"),
                            quantity = 1,
                            title = "Racing the blackout",
                        });
                }

                async Task<HttpResponseMessage> BlackOutAsync()
                {
                    await ready.Task;

                    return await admin.PostAsJsonAsync(
                        $"/resources/{resource}/blackout-periods",
                        new
                        {
                            startsAtUtc = At(9).ToString("o"),
                            endsAtUtc = At(10).ToString("o"),
                            reason = "Racing the booking",
                        });
                }
            }
            finally
            {
                await CleanUpAsync(resource);
            }
        }
    }

    // The same race over an *update* that widens an existing blackout to cover
    // the interval — decision 0001 names widening explicitly, and
    // UpdateBlackoutPeriodCommandRequestHandler re-runs the identical cascade
    // over the new interval, so it needs the identical lock.
    [Fact]
    public async Task BookingRacingBlackoutWiden_NeverLeavesALiveBookingInsideTheBlackout()
    {
        for (var trial = 0; trial < 8; trial++)
        {
            var resource = await CreateBookableResourceAsync(capacity: 1);

            try
            {
                var admin = await AuthenticatedClientAsync(AcmeAdmin);

                // A narrow blackout well clear of the contested slot, then
                // widened onto it during the race — decision 0001's "moving
                // matters as much as widening".
                var created = await admin.PostAsJsonAsync(
                    $"/resources/{resource}/blackout-periods",
                    new
                    {
                        startsAtUtc = At(14).ToString("o"),
                        endsAtUtc = At(15).ToString("o"),
                        reason = "Starts elsewhere",
                    });
                created.EnsureSuccessStatusCode();
                var blackoutId = (await created.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
                    TestJson.Options))!.Id;

                var member = await AuthenticatedClientAsync(AcmeMember);

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var booking = BookAsync();
                var widen = WidenAsync();

                await Task.Delay(GateDelay);
                ready.SetResult();

                var bookingResponse = await booking;
                var widenResponse = await widen;

                Assert.True(
                    bookingResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict
                        or HttpStatusCode.UnprocessableEntity,
                    $"Trial {trial}: the booking answered {(int)bookingResponse.StatusCode}; only 201, 409 "
                    + "or 422 is correct. A 500 here is a deadlock that escaped the 1205 retry.");

                Assert.Equal(HttpStatusCode.OK, widenResponse.StatusCode);

                Assert.Equal(0, await LiveBookingsInsideAnyBlackoutAsync(resource));

                async Task<HttpResponseMessage> BookAsync()
                {
                    await ready.Task;

                    return await member.PostAsJsonAsync(
                        "/bookings",
                        new
                        {
                            resourceId = resource,
                            startsAtUtc = At(9).ToString("o"),
                            endsAtUtc = At(10).ToString("o"),
                            quantity = 1,
                            title = "Racing the widen",
                        });
                }

                async Task<HttpResponseMessage> WidenAsync()
                {
                    await ready.Task;

                    return await admin.PutAsJsonAsync(
                        $"/resources/{resource}/blackout-periods/{blackoutId}",
                        new
                        {
                            startsAtUtc = At(9).ToString("o"),
                            endsAtUtc = At(15).ToString("o"),
                            reason = "Widened onto the contested slot",
                        });
                }
            }
            finally
            {
                await CleanUpAsync(resource);
            }
        }
    }

    // ---- Arrangement and inspection ----------------------------------------

    // The invariant itself: a live (Pending/Confirmed) booking whose interval
    // overlaps any blackout on the same resource — the half-open predicate
    // both dbo.CreateBooking and dbo.LockBookingsForBlackout use, expressed as
    // a query nothing under test could have influenced.
    private static Task<int> LiveBookingsInsideAnyBlackoutAsync(Guid resourceId) =>
        CountAsync(
            """
            SELECT COUNT(*) FROM dbo.Bookings AS b
            WHERE b.ResourceId = @p0
              AND b.Status IN ('Pending', 'Confirmed')
              AND EXISTS (
                  SELECT 1 FROM dbo.BlackoutPeriods AS bp
                  WHERE bp.ResourceId = b.ResourceId
                    AND bp.StartsAtUtc < b.EndsAtUtc
                    AND bp.EndsAtUtc   > b.StartsAtUtc);
            """,
            resourceId);

    private static async Task<int> CountAsync(string sql, params object[] parameters)
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

    // In the UTC zone and open around the clock, matching
    // BookingConcurrencyEndpointTests: the conversion rules have their own
    // thorough tests elsewhere, and a resource whose local time is UTC keeps
    // this file's assertions about contention rather than about arithmetic it
    // would otherwise have to redo.
    private async Task<Guid> CreateBookableResourceAsync(int capacity)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Blackout Race Resource {Guid.NewGuid():N}",
                description = "Created by the blackout/booking concurrency tests",
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

    private async Task CleanUpAsync(Guid resourceId)
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
                DELETE FROM dbo.BlackoutPeriods WHERE ResourceId = @ResourceId;
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
