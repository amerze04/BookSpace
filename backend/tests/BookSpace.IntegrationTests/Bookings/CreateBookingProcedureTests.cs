using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// WP-4 Phase 1b: dbo.CreateBooking, tested directly rather than through an
// endpoint — there is no endpoint yet, and that is deliberate. The procedure is
// where the no-double-booking guarantee lives (CLAUDE.md §4.1, FR-4.2, AC-1), so
// it is proved before anything is built on top of it, at the layer where the
// guarantee actually is. The HTTP-level concurrency suite follows in Phase 3;
// what it adds is the pipeline, not the lock.
//
// **These tests speak raw ADO on purpose.** Parallel HttpClients would exercise
// Kestrel, the mediator and EF as much as the lock, and a failure would not say
// which. N connections calling the procedure at the same instant say exactly one
// thing.
//
// **Every connection sets a real tenant session context**, not an RLS bypass,
// because the procedure's behaviour depends on RLS being in force: it reads
// Resources through the filtered table so that a connection with no context
// fails closed instead of counting zero overlapping bookings. One test below
// asserts precisely that, and it would be meaningless if the others bypassed.
//
// Same state hygiene as the sibling files: each test creates its own resource
// through the real API and removes it again, since the host's database is shared
// across the collection and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class CreateBookingProcedureTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public CreateBookingProcedureTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // A fixed future day, in whole seconds. datetime2(0) rounds on write
    // (CLAUDE.md §4.3), so a fractional instant would move the stored boundary
    // off the one an assertion expects — the trap that turned a WP-3 adjacency
    // test intermittent.
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 3, 15, hour, minute, 0, DateTimeKind.Utc);

    // ---- The single-caller cases -------------------------------------------

    [Fact]
    public async Task CreateBooking_InsertsTheRow()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            var bookingId = Guid.NewGuid();
            var outcome = await CallAsync(resource, bookingId, At(9), At(10), quantity: 2);

            Assert.Equal("Created", outcome.ResultCode);
            Assert.Equal(2, outcome.RemainingCapacity);

            var stored = await ReadBookingAsync(bookingId);
            Assert.Equal(resource.OrgId, stored.OrgId);
            Assert.Equal(resource.Id, stored.ResourceId);
            Assert.Equal("Confirmed", stored.Status);
            Assert.Equal(2, stored.Quantity);
            Assert.Equal(At(9), stored.StartsAtUtc);
            Assert.Equal(At(10), stored.EndsAtUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The clock is passed in, never taken as SYSUTCDATETIME() inside the
    // procedure (CLAUDE.md §4.3) — so the response a handler builds and the row a
    // client reads back carry the same instant.
    [Fact]
    public async Task CreateBooking_StampsTheSuppliedInstantNotTheServerClock()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = Guid.NewGuid();
            var nowUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            await CallAsync(resource, bookingId, At(9), At(10), quantity: 1, nowUtc: nowUtc);

            var stored = await ReadBookingAsync(bookingId);
            Assert.Equal(nowUtc, stored.CreatedAtUtc);
            Assert.Equal(nowUtc, stored.UpdatedAtUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task CreateBooking_RefusesAnArchivedResource()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeAdmin);
            (await client.PostAsync($"/resources/{resource.Id}/archive", null)).EnsureSuccessStatusCode();

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);

            Assert.Equal("ResourceArchived", outcome.ResultCode);
            Assert.Equal(0, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task CreateBooking_RefusesAnUnknownResource()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var phantom = resource with { Id = Guid.NewGuid() };
            var outcome = await CallAsync(phantom, Guid.NewGuid(), At(9), At(10), quantity: 1);

            Assert.Equal("ResourceNotFound", outcome.ResultCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The fail-closed guard, and the reason the procedure reads Resources before
    // it counts anything. Security.TenantAccessPolicy is a *filter* policy: it
    // filters the overlap query but does not block the INSERT, so a connection
    // with no tenant context would see zero overlapping bookings and overbook —
    // a lock correctly taken over an empty set. Because Resources is filtered by
    // the same predicate, that connection cannot see the resource either.
    //
    // The assertion that matters is not just the result code; it is that no row
    // was written.
    [Fact]
    public async Task CreateBooking_WithNoTenantSessionContext_FailsClosed()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var outcome = await CallAsync(
                resource, Guid.NewGuid(), At(9), At(10), quantity: 1, setTenantContext: false);

            Assert.Equal("ResourceNotFound", outcome.ResultCode);
            Assert.Equal(0, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Capacity, sequentially --------------------------------------------

    [Fact]
    public async Task CreateBooking_RefusesAnExclusiveResourceThatIsTaken()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            Assert.Equal("Created", (await CallAsync(resource, Guid.NewGuid(), At(9), At(10), 1)).ResultCode);

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), 1);

            Assert.Equal("SlotUnavailable", outcome.ResultCode);
            Assert.Equal(0, outcome.RemainingCapacity);
            Assert.Equal(1, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The owner's split of 2026-09-07, at the layer that decides it: something is
    // free, just not enough of it.
    [Fact]
    public async Task CreateBooking_DistinguishesNotEnoughRoomFromNoneAtAll()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 3);

            var notEnough = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 2);
            Assert.Equal("CapacityExceeded", notEnough.ResultCode);
            Assert.Equal(1, notEnough.RemainingCapacity);

            await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);

            var noneAtAll = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);
            Assert.Equal("SlotUnavailable", noneAtAll.ResultCode);
            Assert.Equal(0, noneAtAll.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // **The §4.1 correction, as a test.** Capacity 2, one unit held 09:00-10:00
    // and one held 10:00-11:00, a request for 09:30-10:30 of one unit. The two
    // existing bookings never coexist, so one unit is free at every instant of
    // the request and it is legal. Summing the overlapping quantities gives 2 and
    // refuses it — which is what CLAUDE.md §4.1 said to do until 2026-09-07, and
    // what the availability query would already have contradicted by offering the
    // slot.
    [Fact]
    public async Task CreateBooking_MeasuresThePeakNotTheSumOfOverlappingBookings()
    {
        var resource = await CreateResourceAsync(capacity: 2);

        try
        {
            await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);
            await CallAsync(resource, Guid.NewGuid(), At(10), At(11), quantity: 1);

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(9, 30), At(10, 30), quantity: 1);

            Assert.Equal("Created", outcome.ResultCode);
            Assert.Equal(0, outcome.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Half-open intervals, the same convention as everywhere else: a booking
    // ending exactly when the next begins is not an overlap.
    [Fact]
    public async Task CreateBooking_AllowsAnAdjacentBookingOnAnExclusiveResource()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(10), At(11), quantity: 1);

            Assert.Equal("Created", outcome.ResultCode);
            Assert.Equal(2, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Only Pending and Confirmed hold a claim (decision 0005). The four terminal
    // statuses are the case a naive overlap query gets wrong, so all four are
    // asserted rather than one standing in for the rest.
    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Rejected")]
    [InlineData("NoShow")]
    [InlineData("Completed")]
    public async Task CreateBooking_IgnoresBookingsInATerminalStatus(string status)
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            await InsertBookingDirectlyAsync(resource, At(9), At(10), quantity: 1, status: status);

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);

            Assert.Equal("Created", outcome.ResultCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task CreateBooking_CountsAPendingBookingAgainstCapacity()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1, status: "Pending");

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);

            Assert.Equal("SlotUnavailable", outcome.ResultCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Decision 0001's absolute priority, re-checked under the lock because the
    // blackout cascade can run between the handler's check and this INSERT and
    // miss a booking that does not exist yet.
    [Fact]
    public async Task CreateBooking_RefusesAnIntervalCoveredByABlackout()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeAdmin);
            var blackout = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                new
                {
                    startsAtUtc = At(8).ToString("o"),
                    endsAtUtc = At(12).ToString("o"),
                    reason = "Procedure test",
                });
            blackout.EnsureSuccessStatusCode();

            var outcome = await CallAsync(resource, Guid.NewGuid(), At(9), At(10), quantity: 1);

            Assert.Equal("BlackoutPeriod", outcome.ResultCode);
            Assert.Equal(0, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- The acceptance criterion (AC-1) -----------------------------------

    // **The single most important test in the codebase** (CLAUDE.md §8). One
    // remaining slot, two requests at the same instant: exactly one succeeds, the
    // other is told why, and exactly one row exists afterwards. Never both.
    [Fact]
    public async Task TwoSimultaneousRequestsForOneSlot_ExactlyOneSucceeds()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var outcomes = await RaceAsync(resource, attempts: 2, quantity: 1);

            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "Created"));
            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "SlotUnavailable"));
            Assert.Equal(1, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Wider than the acceptance criterion asks for: the guarantee should not
    // depend on there being exactly two contenders.
    [Fact]
    public async Task TwentySimultaneousRequestsForOneSlot_ExactlyOneSucceeds()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var outcomes = await RaceAsync(resource, attempts: 20, quantity: 1);

            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "Created"));
            Assert.Equal(19, outcomes.Count(o => o.ResultCode == "SlotUnavailable"));
            Assert.Equal(1, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The pooled case, which is what proves decision 0005's model rather than
    // mere mutual exclusion: a capacity of four admits exactly four concurrent
    // single-unit bookings and refuses the fifth onwards.
    [Fact]
    public async Task ConcurrentRequestsFillAPoolExactlyToItsCapacity()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            var outcomes = await RaceAsync(resource, attempts: 10, quantity: 1);

            Assert.Equal(4, outcomes.Count(o => o.ResultCode == "Created"));
            Assert.Equal(6, outcomes.Count(o => o.ResultCode == "SlotUnavailable"));
            Assert.Equal(4, await CountBookingsAsync(resource.Id));
            Assert.Equal(4, await SumQuantityAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Unequal claims on one pool. Capacity 4 with two requests for 3 units: they
    // do not both fit, so exactly one may win — and the loser is told
    // CapacityExceeded rather than SlotUnavailable, because a unit is still free.
    [Fact]
    public async Task ConcurrentRequestsForMostOfAPool_ExactlyOneSucceeds()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            var outcomes = await RaceAsync(resource, attempts: 2, quantity: 3);

            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "Created"));
            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "CapacityExceeded"));
            Assert.Equal(3, await SumQuantityAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The lock must not over-refuse. Twelve concurrent bookings at twelve
    // different hours on an exclusive resource all belong — if the range lock
    // serialised them into failures rather than into a queue, this is where that
    // would show.
    [Fact]
    public async Task ConcurrentRequestsForDifferentHoursAllSucceed()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var attempts = Enumerable.Range(8, 12)
                .Select(hour => CallAsync(resource, Guid.NewGuid(), At(hour), At(hour + 1), quantity: 1));

            var outcomes = await Task.WhenAll(attempts);

            Assert.All(outcomes, o => Assert.Equal("Created", o.ResultCode));
            Assert.Equal(12, await CountBookingsAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Fires `attempts` calls for the same slot as close to simultaneously as the
    // client can manage: every connection is opened and its tenant context set
    // *before* any of them calls the procedure, so the race is over the lock and
    // not over connection setup.
    private async Task<IReadOnlyList<ProcedureOutcome>> RaceAsync(
        TestResource resource,
        int attempts,
        int quantity)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = Enumerable.Range(0, attempts)
            .Select(async _ =>
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                await SetTenantContextAsync(connection, resource.OrgId);

                await ready.Task;

                return await ExecuteAsync(
                    connection, resource, Guid.NewGuid(), At(9), At(10), quantity, "Confirmed", At(0));
            })
            .ToList();

        // Let every racer reach the gate before opening it.
        await Task.Delay(200);
        ready.SetResult();

        return await Task.WhenAll(racers);
    }

    // ---- Calling the procedure ---------------------------------------------

    private async Task<ProcedureOutcome> CallAsync(
        TestResource resource,
        Guid bookingId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int quantity,
        string status = "Confirmed",
        DateTime? nowUtc = null,
        bool setTenantContext = true)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        if (setTenantContext)
        {
            await SetTenantContextAsync(connection, resource.OrgId);
        }

        return await ExecuteAsync(
            connection, resource, bookingId, startsAtUtc, endsAtUtc, quantity, status, nowUtc ?? At(0));
    }

    // **Retries a deadlock victim, because production does.** Every real call to
    // this procedure goes through IUnitOfWork, which runs inside
    // Database.CreateExecutionStrategy() with 1205 in the retryable set
    // (CLAUDE.md §5) — so a test calling it over a raw connection with no retry
    // would be exercising a configuration that does not exist anywhere in the
    // application, and would fail for a reason production has already handled.
    //
    // This is not papering over a flake. Decision 0023 says 1205 is an expected
    // outcome rather than a fault, and one was in fact observed here at ten-way
    // contention (2026-09-07) — the U-mode range locks turn ordinary two-way
    // contention into blocking, but they do not make a deadlock impossible, and
    // the blackout re-check inverts lock order against the cascade besides.
    // Retrying is the design; the assertion that matters is that exactly the
    // right number of bookings exist at the end, however many attempts that took.
    //
    // The booking id is passed in and reused across attempts, exactly as
    // production reuses it — a retry must re-insert the same row, not a second
    // one.
    private static async Task<ProcedureOutcome> ExecuteAsync(
        SqlConnection connection,
        TestResource resource,
        Guid bookingId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int quantity,
        string status,
        DateTime nowUtc)
    {
        const int deadlockVictim = 1205;
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(
                    connection, resource, bookingId, startsAtUtc, endsAtUtc, quantity, status, nowUtc);
            }
            catch (SqlException e) when (e.Number == deadlockVictim && attempt < maxAttempts)
            {
                // A short back-off, so the retry does not collide with the same
                // contender again immediately. EF's strategy does the same.
                await Task.Delay(attempt * 20);
            }
        }
    }

    private static async Task<ProcedureOutcome> ExecuteOnceAsync(
        SqlConnection connection,
        TestResource resource,
        Guid bookingId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int quantity,
        string status,
        DateTime nowUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "dbo.CreateBooking";
        command.CommandType = System.Data.CommandType.StoredProcedure;

        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ResourceId", resource.Id);
        command.Parameters.AddWithValue("@UserId", resource.MemberUserId);
        command.Parameters.AddWithValue("@StartsAtUtc", startsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", endsAtUtc);
        command.Parameters.AddWithValue("@Quantity", quantity);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@CreatedByUserId", resource.MemberUserId);
        command.Parameters.AddWithValue("@NowUtc", nowUtc);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "dbo.CreateBooking returned no result row.");

        return new ProcedureOutcome(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private sealed record ProcedureOutcome(string ResultCode, int? RemainingCapacity);

    // The same signal TenantSessionContextInterceptor sends on a real request
    // (CLAUDE.md §4.2, decision 0013). A real OrgId rather than TenantBypass, so
    // these tests run the path production runs.
    private static async Task SetTenantContextAsync(SqlConnection connection, Guid orgId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'TenantInit',   @value = 1;
            EXEC sp_set_session_context @key = N'TenantBypass', @value = 0;
            EXEC sp_set_session_context @key = N'OrgId',        @value = @orgId;
            """;
        command.Parameters.AddWithValue("@orgId", orgId);
        await command.ExecuteNonQueryAsync();
    }

    // ---- Arrangement and inspection ----------------------------------------

    private sealed record TestResource(Guid Id, Guid OrgId, Guid MemberUserId);

    private async Task<TestResource> CreateResourceAsync(int capacity)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Procedure Test {Guid.NewGuid():N}",
                description = "Created by the CreateBooking procedure tests",
                resourceType = "Room",
                capacity,
                timeZoneId = "UTC",
                requiresApproval = false,
                minDurationMinutes = (int?)null,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();

        var created = (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;

        return new TestResource(created.Id, await OrgIdOfAsync(created.Id), await MemberIdAsync());
    }

    private static async Task<Guid> OrgIdOfAsync(Guid resourceId) =>
        await ScalarAsync<Guid>("SELECT OrgId FROM dbo.Resources WHERE Id = @p0;", resourceId);

    private static async Task<Guid> MemberIdAsync() =>
        await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeMember);

    private static async Task<int> CountBookingsAsync(Guid resourceId) =>
        await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Bookings WHERE ResourceId = @p0 AND Status IN ('Pending','Confirmed');",
            resourceId);

    private static async Task<int> SumQuantityAsync(Guid resourceId) =>
        await ScalarAsync<int>(
            """
            SELECT ISNULL(SUM(Quantity), 0) FROM dbo.Bookings
            WHERE ResourceId = @p0 AND Status IN ('Pending','Confirmed');
            """,
            resourceId);

    private sealed record StoredBooking(
        Guid OrgId,
        Guid ResourceId,
        string Status,
        int Quantity,
        DateTime StartsAtUtc,
        DateTime EndsAtUtc,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private static async Task<StoredBooking> ReadBookingAsync(Guid bookingId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrgId, ResourceId, Status, Quantity, StartsAtUtc, EndsAtUtc, CreatedAtUtc, UpdatedAtUtc
            FROM dbo.Bookings WHERE Id = @BookingId;
            """;
        command.Parameters.AddWithValue("@BookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");

        return new StoredBooking(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt32(3),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc));
    }

    // Decision 0017's carve-out, still the only way to put a booking into a
    // terminal status: the procedure creates Pending and Confirmed rows only, and
    // no transition path exists until Phase 2's cancel.
    private static async Task InsertBookingDirectlyAsync(
        TestResource resource,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int quantity,
        string status)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.Bookings
                (Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Status,
                 CreatedAtUtc, CreatedByUserId, UpdatedAtUtc)
            VALUES
                (NEWID(), @OrgId, @ResourceId, @UserId, @StartsAtUtc, @EndsAtUtc, @Quantity, @Status,
                 @NowUtc, @UserId, @NowUtc);
            """;
        command.Parameters.AddWithValue("@OrgId", resource.OrgId);
        command.Parameters.AddWithValue("@ResourceId", resource.Id);
        command.Parameters.AddWithValue("@UserId", resource.MemberUserId);
        command.Parameters.AddWithValue("@StartsAtUtc", startsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", endsAtUtc);
        command.Parameters.AddWithValue("@Quantity", quantity);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@NowUtc", At(0));

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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

    private static async Task EnterRlsBypassAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'TenantInit',   @value = 1;
            EXEC sp_set_session_context @key = N'TenantBypass', @value = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    // Bookings before the resource: FK_Bookings_Resources_SameOrg is NoAction
    // (CLAUDE.md §4.5), so the resource cannot go while a booking references it.
    // Blackouts cascade with the resource.
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

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }
}
