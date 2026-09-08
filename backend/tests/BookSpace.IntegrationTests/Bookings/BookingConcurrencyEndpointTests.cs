using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// WP-4 Phase 3: **AC-1 at the HTTP level** — the work package's hard problem,
// and CLAUDE.md §8's "single most important test in the codebase", fired through
// the real pipeline instead of at the procedure.
//
// CreateBookingProcedureTests already proves the guarantee at the layer where it
// lives: parallel raw connections calling dbo.CreateBooking, exactly one row.
// **What this file adds is the pipeline, not the lock** — Kestrel's test host,
// the JwtBearer handler, the mediator, EF's transaction and its execution
// strategy, all of the things a raw ADO race deliberately excludes. Two
// properties can only be observed here:
//
//   1. **No 500s.** A deadlock (1205) that escapes IUnitOfWork's retry surfaces
//      as a 500 to a client and as nothing at all to the procedure-level suite,
//      which runs its own retry loop over a raw connection. Every test below
//      therefore asserts that every response is 201 or 409 — never anything
//      else — which is the assertion that makes decision 0023's point 4 ("1205
//      is absorbed, not avoided") a measured claim rather than a hope.
//   2. **A cancel racing a create**, which Phase 2b made possible and a
//      procedure-level test cannot express: cancelling goes through EF and
//      SaveChanges, not through dbo.CreateBooking, so the two paths only meet
//      above the repository.
//
// **The gate is load-bearing, and not merely for timing.**
// CreateBookingCommandRequestHandler checks capacity *before* the unit of work
// opens, so a contender arriving after the winner has committed is refused by
// that pre-check and never contends for the lock at all. A race whose requests
// trickle would therefore pass with the lock hints removed — it would be
// measuring the pre-check. Every racer here logs in and builds its payload
// before a TaskCompletionSource releases them together, exactly as
// CreateBookingProcedureTests.RaceAsync opens its connections before calling.
//
// **The reason codes are deterministic, which is why they are asserted
// exactly.** Both throw sites — the handler's pre-check and the procedure's
// answer — raise the same exception types, and CapacityExceededException's
// RemainingCapacity is log-only (decision 0016), so a client cannot tell which
// layer refused it and no assertion here can flake on that. Capacity 1 admits no
// quantity but 1, so its losers can only ever be SlotUnavailable; a pool refuses
// with CapacityExceeded while something is still free, and SlotUnavailable once
// nothing is.
//
// Same state hygiene as its siblings: every test creates its own resource and
// removes it again, since the host's database is shared across the collection
// and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingConcurrencyEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";

    // Three distinct Acme accounts, cycled across the racers so that no race is
    // won by virtue of who asked. Deliberately not member2@acme.test, which
    // AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin deactivates
    // permanently — a test using it passes alone and fails only in a full run,
    // with a 401 on *login*. An Approver and a TenantAdmin can both book (both
    // satisfy the TenantMember policy), so all three are legitimate bookers.
    private static readonly string[] Racers = [AcmeMember, AcmeApprover, AcmeAdmin];

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public BookingConcurrencyEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // A fixed future day in whole seconds — datetime2(0) rounds on write
    // (CLAUDE.md §4.3), and the validator refuses fractions anyway. Its own day,
    // distinct from the sibling files', so a stray row from a failed cleanup
    // there cannot silently join a race here.
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 4, 8, hour, minute, 0, DateTimeKind.Utc);

    // ---- The acceptance criterion (AC-1) -----------------------------------

    // **The criterion, verbatim**: one remaining slot, two simultaneous
    // requests, exactly one succeeds and the other gets a clear rejection —
    // never both. Two different members, because that is the situation the PRD
    // describes.
    [Fact]
    public async Task TwoSimultaneousRequestsForOneSlot_ExactlyOneSucceeds()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var attempts = await RaceAsync(resource, SameSlot(2));

            AssertOnlyCreatedOrRefused(attempts);
            Assert.Single(attempts, a => a.Status == HttpStatusCode.Created);
            Assert.Single(attempts, a => a.ReasonCode == "SlotUnavailable");
            Assert.Equal(1, await CountBookingsAsync(resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Wider than the criterion asks: the guarantee must not depend on there
    // being exactly two contenders.
    //
    // Ten rather than the procedure suite's twenty, deliberately. Each request
    // here is a whole pipeline plus a transaction that serialises against the
    // others (decision 0023's accepted consequence: concurrent creates on one
    // resource queue), so twenty would double the runtime to re-prove at this
    // layer what CreateBookingProcedureTests already proves at twenty. Ten is
    // past the point where 0023 observed its first 1205, which is what this file
    // is here to catch.
    [Fact]
    public async Task TenSimultaneousRequestsForOneSlot_ExactlyOneSucceeds()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var attempts = await RaceAsync(resource, SameSlot(10));

            AssertOnlyCreatedOrRefused(attempts);
            Assert.Single(attempts, a => a.Status == HttpStatusCode.Created);
            Assert.Equal(9, attempts.Count(a => a.ReasonCode == "SlotUnavailable"));
            Assert.Equal(1, await CountBookingsAsync(resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The pooled case, which proves decision 0005's concurrent-units model
    // rather than mere mutual exclusion: a capacity of four admits exactly four
    // simultaneous single-unit bookings and refuses the rest. A guarantee built
    // on "one booking per slot" would fail this in the opposite direction, by
    // refusing three legitimate bookings.
    [Fact]
    public async Task ConcurrentRequestsFillAPoolExactlyToItsCapacity()
    {
        var resource = await CreateBookableResourceAsync(capacity: 4);

        try
        {
            var attempts = await RaceAsync(resource, SameSlot(8));

            AssertOnlyCreatedOrRefused(attempts);
            Assert.Equal(4, attempts.Count(a => a.Status == HttpStatusCode.Created));
            Assert.Equal(4, attempts.Count(a => a.ReasonCode == "SlotUnavailable"));
            Assert.Equal(4, await CountBookingsAsync(resource));
            Assert.Equal(4, await LiveQuantityAsync(resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Unequal claims on one pool: capacity 4, one request for three units and
    // one for two. They do not both fit, so exactly one may win — and the loser
    // is told CapacityExceeded rather than SlotUnavailable, because units are
    // still free, just fewer than it asked for (smaller call 1).
    //
    // Deterministic whichever wins: the remainder is 1 or 2, both above zero and
    // below the loser's ask.
    [Fact]
    public async Task ConcurrentRequestsForMostOfAPool_ExactlyOneSucceeds()
    {
        var resource = await CreateBookableResourceAsync(capacity: 4);

        try
        {
            var attempts = await RaceAsync(
                resource,
                [
                    new Racer(AcmeMember, At(9), At(10), Quantity: 3),
                    new Racer(AcmeApprover, At(9), At(10), Quantity: 2),
                ]);

            AssertOnlyCreatedOrRefused(attempts);
            Assert.Single(attempts, a => a.Status == HttpStatusCode.Created);
            Assert.Single(attempts, a => a.ReasonCode == "CapacityExceeded");
            Assert.Equal(1, await CountBookingsAsync(resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The lock must not over-refuse. Decision 0023 accepts an **open lower
    // bound** on the locked range — a narrowing bound could miss a booking made
    // before the duration limit existed and silently overbook — so concurrent
    // creates on one resource serialise whether or not their times overlap. The
    // claim is that they *queue*, not that they fail, and this is where a
    // regression in that claim would show.
    [Fact]
    public async Task ConcurrentRequestsForDifferentHoursAllSucceed()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var racers = Enumerable.Range(8, 12)
                .Select((hour, index) => new Racer(
                    Racers[index % Racers.Length], At(hour), At(hour + 1), Quantity: 1))
                .ToList();

            var attempts = await RaceAsync(resource, racers);

            AssertOnlyCreatedOrRefused(attempts);
            Assert.All(attempts, a => Assert.Equal(HttpStatusCode.Created, a.Status));
            Assert.Equal(12, await CountBookingsAsync(resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- A cancel racing a create (only expressible above the repository) ---

    // The one race the procedure-level suite cannot express, and Phase 2b is
    // what made it possible: cancelling is **not** a §4.1 write path — it goes
    // through EF and one SaveChangesAsync, because it can only ever *reduce* the
    // units held at an instant — so a cancel and a create meet nowhere below
    // this layer.
    //
    // **The assertion is an invariant, not an outcome.** Both results are
    // correct: the challenger may see the freed unit (201) or may not (409),
    // depending on which transaction commits first, and no test may prefer one.
    // What must hold either way is that the resource is never over-committed,
    // that the cancel is never the casualty, and that nothing 500s.
    //
    // It is also the likeliest 1205 in the codebase: the cancel's UPDATE takes an
    // exclusive lock on a row inside the very key range the create locks with
    // RangeS-U, in the opposite order. Both sides retry — the create through
    // IUnitOfWork, the cancel through the execution strategy EF applies to
    // SaveChanges — which is precisely decision 0023's point 4.
    [Fact]
    public async Task ACancelRacingACreateForTheFreedSlot_NeverDoubleBooks()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var owner = await AuthenticatedClientAsync(AcmeMember);
            var held = await CreateBookingAsync(owner, resource, At(9), At(10));

            var challenger = await AuthenticatedClientAsync(AcmeApprover);
            var payload = Booking(resource, At(9), At(10));

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var cancelling = Cancel();
            var creating = Create();

            // Both are parked on the gate before it opens.
            await Task.Delay(GateDelay);
            ready.SetResult();

            var cancelResponse = await cancelling;
            var createResponse = await creating;

            // The cancel is never the casualty: it contends for a row it already
            // owns, and RowVersion protects it from nothing the create does.
            Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

            Assert.True(
                createResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"The create answered {(int)createResponse.StatusCode}; only 201 or 409 is correct. "
                + "A 500 here is a deadlock that escaped the 1205 retry.");

            if (createResponse.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("SlotUnavailable", await ReasonCodeAsync(createResponse));
            }

            // The invariant. Capacity 1, so at most one live booking may overlap
            // the interval no matter how the race resolved.
            Assert.True(
                await LiveOverlappingCountAsync(resource, At(9), At(10)) <= 1,
                "Two live bookings overlap a slot on an exclusive resource.");

            // And the cancelled booking is still there — nothing is deleted
            // (CLAUDE.md §4.5), so the freed unit is a status change rather than
            // a vanished row.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Cancelled';",
                held.Id));

            async Task<HttpResponseMessage> Cancel()
            {
                await ready.Task;

                return await owner.PostAsJsonAsync(
                    $"/bookings/{held.Id}/cancel", new { reason = "Freeing the room" });
            }

            async Task<HttpResponseMessage> Create()
            {
                await ready.Task;

                return await challenger.PostAsJsonAsync("/bookings", payload);
            }
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Running a race ----------------------------------------------------

    // Long enough for every racer's continuation to be parked on the gate before
    // it opens, matching CreateBookingProcedureTests. It is a setup cost, not a
    // timing assumption: the outcome does not depend on the requests arriving
    // within any particular span, only on their arriving before any of them has
    // committed.
    private static readonly TimeSpan GateDelay = TimeSpan.FromMilliseconds(200);

    private sealed record Racer(string Email, DateTime Start, DateTime End, int Quantity);

    private sealed record Attempt(HttpStatusCode Status, string? ReasonCode, Guid? BookingId);

    // N racers for one interval, cycling the accounts.
    private static IReadOnlyList<Racer> SameSlot(int count, int quantity = 1) =>
        Enumerable.Range(0, count)
            .Select(index => new Racer(Racers[index % Racers.Length], At(9), At(10), quantity))
            .ToList();

    // Fires every racer's POST /bookings as close to simultaneously as a client
    // can manage. **Everything that is not the request itself happens before the
    // gate**: one login per distinct account (PBKDF2 at 100k iterations would
    // otherwise stagger the arrivals by more than the race lasts), the HttpClient,
    // and the payload.
    private async Task<IReadOnlyList<Attempt>> RaceAsync(
        Guid resourceId,
        IReadOnlyList<Racer> racers)
    {
        var tokens = new Dictionary<string, string>();

        foreach (var email in racers.Select(r => r.Email).Distinct())
        {
            tokens[email] = await AccessTokenAsync(email);
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = racers
            .Select(async racer =>
            {
                var client = ClientWithToken(tokens[racer.Email]);
                var payload = Booking(resourceId, racer.Start, racer.End, racer.Quantity);

                await ready.Task;

                return await AttemptAsync(client, payload);
            })
            .ToList();

        // Let every racer reach the gate before opening it.
        await Task.Delay(GateDelay);
        ready.SetResult();

        return await Task.WhenAll(attempts);
    }

    private static async Task<Attempt> AttemptAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/bookings", payload);

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var body = (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
                TestJson.Options))!;

            return new Attempt(response.StatusCode, ReasonCode: null, body.Id);
        }

        return new Attempt(response.StatusCode, await ReasonCodeAsync(response), BookingId: null);
    }

    // **The assertion that only exists at this layer.** A booking request may be
    // accepted or refused; anything else means the pipeline failed rather than
    // the rules, and a 1205 escaping IUnitOfWork's retry is the specific failure
    // this guards. Called by every race above, so no test can forget it.
    private static void AssertOnlyCreatedOrRefused(IEnumerable<Attempt> attempts) =>
        Assert.All(attempts, attempt => Assert.True(
            attempt.Status is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"A racer answered {(int)attempt.Status}; only 201 or 409 is correct. "
            + "A 500 here is a deadlock that escaped the 1205 retry, or a broken pipeline."));

    // ---- Arrangement and inspection ----------------------------------------

    private static object Booking(Guid resourceId, DateTime start, DateTime end, int quantity = 1) =>
        new
        {
            resourceId,
            startsAtUtc = start.ToString("o"),
            endsAtUtc = end.ToString("o"),
            quantity,
            title = "Contended slot",
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

    // In the UTC zone and open around the clock, for the reason the sibling files
    // give: the conversion rules have their own thorough tests, and a resource
    // whose local time is UTC keeps these assertions about contention rather than
    // about arithmetic the test would have to redo.
    private async Task<Guid> CreateBookableResourceAsync(int capacity)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Contended Resource {Guid.NewGuid():N}",
                description = "Created by the booking concurrency endpoint tests",
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

        // Open every day. 23:59:59 is this system's spelling of midnight
        // (decision 0022), so the day is continuous.
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

    private static Task<int> CountBookingsAsync(Guid resourceId) =>
        CountAsync("SELECT COUNT(*) FROM dbo.Bookings WHERE ResourceId = @p0;", resourceId);

    // The units actually held, not the row count — the two differ on a pool, and
    // conflating them is how a capacity bug hides.
    private static Task<int> LiveQuantityAsync(Guid resourceId) =>
        CountAsync(
            """
            SELECT ISNULL(SUM(Quantity), 0) FROM dbo.Bookings
            WHERE ResourceId = @p0 AND Status IN ('Pending', 'Confirmed');
            """,
            resourceId);

    // Half-open overlap on both sides, the same predicate the procedure and
    // BlackoutPeriod.Overlaps use, and only the two statuses that hold a claim
    // (decision 0005).
    private static Task<int> LiveOverlappingCountAsync(Guid resourceId, DateTime start, DateTime end) =>
        CountAsync(
            """
            SELECT COUNT(*) FROM dbo.Bookings
            WHERE ResourceId = @p0
              AND Status IN ('Pending', 'Confirmed')
              AND StartsAtUtc < @p2
              AND EndsAtUtc   > @p1;
            """,
            resourceId, start, end);

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

    // Decision 0017's gotcha: without an explicit bypass the fixture connection
    // has no tenant session context, so the RLS predicate hides every row and a
    // count reads zero while the rows plainly exist.
    private static async Task EnterRlsBypassAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'TenantInit',   @value = 1;
            EXEC sp_set_session_context @key = N'TenantBypass', @value = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    // Notifications and approval requests reference Bookings; Bookings reference
    // Resources with NoAction (CLAUDE.md §4.5), so the order matters. A leaked
    // resource breaks the other files' counts.
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

    private async Task<HttpClient> AuthenticatedClientAsync(string email) =>
        ClientWithToken(await AccessTokenAsync(email));

    private HttpClient ClientWithToken(string accessToken)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    private async Task<string> AccessTokenAsync(string email)
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = SeedData.SeedPassword });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("accessToken").GetString()!;
    }
}
