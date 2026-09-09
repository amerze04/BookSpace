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

// WP-5 Phase 3: dbo.ApproveBooking, tested directly rather than through an
// endpoint — the same reasoning CreateBookingProcedureTests gives, at the
// same layer the guarantee actually lives (FR-7.5, AC-5, decision 0023
// inherited whole). The HTTP-level suite follows once the endpoint exists;
// what it adds is the pipeline, not the lock.
//
// **What this file can and cannot demonstrate.** dbo.ApproveBooking's
// capacity re-check turns out, by construction, to almost never have
// anything to refuse through legitimate application paths: a Pending booking
// already reserves its units in full (decision 0005), a capacity decrease
// that would strand it is refused at the resource-edit boundary
// (CapacityBelowExistingBookingsException, which counts Pending bookings),
// and dbo.CreateBooking's own lock means no competing booking can ever be
// created that would make a still-Pending approval retroactively invalid.
// So the tests below prove three things a real race genuinely can produce —
// two concurrent decisions on the *same* booking, and an approval safely
// losing to a concurrent cancel of the same row — and prove the capacity
// arithmetic itself is correct by constructing an over-capacity state with
// raw SQL the application cannot reach on its own, the same carve-out
// CreateBookingProcedureTests already uses for terminal-status bookings.
[Collection(nameof(AuthenticationTestCollection))]
public class ApproveBookingProcedureTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public ApproveBookingProcedureTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 4, 12, hour, minute, 0, DateTimeKind.Utc);

    // ---- The single-caller cases -------------------------------------------

    [Fact]
    public async Task ApproveBooking_ConfirmsAPendingBooking()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            var approverId = await ApproverIdAsync();

            var outcome = await CallApproveAsync(resource, bookingId, approverId);

            Assert.Equal("Approved", outcome.ResultCode);
            Assert.Equal(0, outcome.RemainingCapacity);

            var stored = await ReadBookingAsync(bookingId);
            Assert.Equal("Confirmed", stored.Status);
            Assert.Equal(approverId, stored.UpdatedByUserId);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task ApproveBooking_StampsTheSuppliedInstantNotTheServerClock()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            var approverId = await ApproverIdAsync();
            var nowUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            await CallApproveAsync(resource, bookingId, approverId, nowUtc);

            var stored = await ReadBookingAsync(bookingId);
            Assert.Equal(nowUtc, stored.UpdatedAtUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    [InlineData("Rejected")]
    public async Task ApproveBooking_RefusesABookingThatIsNotPending(string status)
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            if (status != "Pending")
            {
                await SetStatusDirectlyAsync(bookingId, status);
            }

            var outcome = await CallApproveAsync(resource, bookingId, await ApproverIdAsync());

            Assert.Equal("BookingNotPending", outcome.ResultCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task ApproveBooking_RefusesAnUnknownBooking()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var outcome = await CallApproveAsync(resource, Guid.NewGuid(), await ApproverIdAsync());

            Assert.Equal("BookingNotPending", outcome.ResultCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task ApproveBooking_RefusesAnArchivedResource()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/resources/{resource.Id}/archive", null)).EnsureSuccessStatusCode();

            var outcome = await CallApproveAsync(resource, bookingId, await ApproverIdAsync());

            Assert.Equal("ResourceArchived", outcome.ResultCode);
            Assert.Equal("Pending", (await ReadBookingAsync(bookingId)).Status);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Decision 0001's absolute priority, re-checked under the lock for the same
    // reason dbo.CreateBooking does: between the handler's own check and this
    // procedure's lock, a blackout cascade could run.
    [Fact]
    public async Task ApproveBooking_RefusesAnIntervalCoveredByABlackout()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                new
                {
                    startsAtUtc = At(8).ToString("o"),
                    endsAtUtc = At(12).ToString("o"),
                    reason = "Procedure test",
                })).EnsureSuccessStatusCode();

            var outcome = await CallApproveAsync(resource, bookingId, await ApproverIdAsync());

            // decision 0001's cascade also cancels the booking itself the
            // moment the blackout is created, so by the time this call runs
            // the row may already be Cancelled — either refusal is correct,
            // and both mean the same thing: nothing gets confirmed inside a
            // blackout.
            Assert.Contains(outcome.ResultCode, new[] { "BlackoutPeriod", "BookingNotPending" });
            Assert.NotEqual("Confirmed", (await ReadBookingAsync(bookingId)).Status);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- The capacity arithmetic, forced by a state the application itself
    // cannot produce (decision 0017's carve-out, reapplied) ------------------

    // A Pending booking always reserves its own units in full (decision 0005),
    // so no *ordinary* create can ever push a still-Pending booking's own slot
    // over capacity — dbo.CreateBooking's own lock already refuses anything
    // that would. This constructs the one state that can: an extra Confirmed
    // row inserted directly, bypassing the procedure, so the peak the lock
    // computes genuinely exceeds capacity by the time the approval runs.
    [Fact]
    public async Task ApproveBooking_RefusesWhenAnOutOfBandBookingHasExceededCapacity()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            await InsertBookingDirectlyAsync(resource, At(9), At(10), quantity: 1, status: "Confirmed");

            var outcome = await CallApproveAsync(resource, bookingId, await ApproverIdAsync());

            Assert.Equal("SlotUnavailable", outcome.ResultCode);
            Assert.Equal(0, outcome.RemainingCapacity);
            Assert.Equal("Pending", (await ReadBookingAsync(bookingId)).Status);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The other half of the split: something is free, just not enough of it.
    [Fact]
    public async Task ApproveBooking_DistinguishesNotEnoughRoomFromNoneAtAll()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 3, status: "Pending");
            await InsertBookingDirectlyAsync(resource, At(9), At(10), quantity: 2, status: "Confirmed");

            var outcome = await CallApproveAsync(resource, bookingId, await ApproverIdAsync());

            Assert.Equal("CapacityExceeded", outcome.ResultCode);
            Assert.Equal(2, outcome.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Concurrency: the guarantees a real race can actually produce ------

    // The UPDLOCK point-lookup's whole job: two approvers (or an approver and
    // a retried request) deciding the same booking at the same instant must
    // not both succeed.
    [Fact]
    public async Task TwoSimultaneousApprovalsOfTheSameBooking_ExactlyOneSucceeds()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            var approverId = await ApproverIdAsync();

            var outcomes = await RaceApprovalsAsync(resource, bookingId, approverId, attempts: 2);

            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "Approved"));
            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "BookingNotPending"));
            Assert.Equal("Confirmed", (await ReadBookingAsync(bookingId)).Status);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Wider than two, on the same reasoning CreateBookingProcedureTests widens
    // its own version of this test: the guarantee should not depend on there
    // being exactly two contenders.
    [Fact]
    public async Task TenSimultaneousApprovalsOfTheSameBooking_ExactlyOneSucceeds()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            var approverId = await ApproverIdAsync();

            var outcomes = await RaceApprovalsAsync(resource, bookingId, approverId, attempts: 10);

            Assert.Equal(1, outcomes.Count(o => o.ResultCode == "Approved"));
            Assert.Equal(9, outcomes.Count(o => o.ResultCode == "BookingNotPending"));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // An approve racing a concurrent cancel of the *same* row (the literal
    // scenario wp5-plan.md §5.3 opens with, minus the sequential gap): exactly
    // one of the two operations may actually change the row, and the row must
    // never end up anywhere but Confirmed or Cancelled — never, for instance,
    // Confirmed with a cancellation reason still pending underneath it.
    [Fact]
    public async Task ApproveRacingAConcurrentCancelOfTheSameBooking_TheyNeverBothWin()
    {
        var resource = await CreateResourceAsync(capacity: 1);

        try
        {
            var bookingId = await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending");
            var approverId = await ApproverIdAsync();

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var approveTask = RaceOneAsync(async () =>
            {
                await ready.Task;
                return await CallApproveAsync(resource, bookingId, approverId);
            });

            var cancelTask = RaceOneAsync(async () =>
            {
                await ready.Task;
                var rows = await CancelIfPendingDirectlyAsync(bookingId);
                return new ProcedureOutcome(rows == 1 ? "Cancelled" : "AlreadyDecided", null);
            });

            await Task.Delay(200);
            ready.SetResult();

            var approveOutcome = await approveTask;
            var cancelOutcome = await cancelTask;

            // Never both: if the cancel actually flipped the row, the approve
            // must not have confirmed it, and vice versa.
            var approveWon = approveOutcome.ResultCode == "Approved";
            var cancelWon = cancelOutcome.ResultCode == "Cancelled";
            Assert.False(approveWon && cancelWon, "Both a cancel and an approve reported success for the same booking.");
            Assert.True(approveWon || cancelWon, "Neither operation made progress — the race produced no outcome at all.");

            var finalStatus = (await ReadBookingAsync(bookingId)).Status;
            Assert.Equal(approveWon ? "Confirmed" : "Cancelled", finalStatus);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The lock must not over-refuse: a pool filled exactly to capacity by
    // Pending bookings admits approving every one of them concurrently,
    // because approving changes nothing about how much capacity is held
    // (decision 0005 — Pending and Confirmed count identically).
    [Fact]
    public async Task ConcurrentApprovalsOfDifferentPendingBookingsExactlyFillingAPool_AllSucceed()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        try
        {
            var bookingIds = new List<Guid>();
            for (var i = 0; i < 4; i++)
            {
                bookingIds.Add(await CreateBookingAsync(resource, At(9), At(10), quantity: 1, status: "Pending"));
            }

            var approverId = await ApproverIdAsync();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = bookingIds
                .Select(id => RaceOneAsync(async () =>
                {
                    await ready.Task;
                    return await CallApproveAsync(resource, id, approverId);
                }))
                .ToList();

            await Task.Delay(200);
            ready.SetResult();

            var outcomes = await Task.WhenAll(tasks);

            Assert.All(outcomes, o => Assert.Equal("Approved", o.ResultCode));
            Assert.Equal(4, await CountConfirmedAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Racing helpers ------------------------------------------------------

    private async Task<IReadOnlyList<ProcedureOutcome>> RaceApprovalsAsync(
        TestResource resource, Guid bookingId, Guid approverId, int attempts)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = Enumerable.Range(0, attempts)
            .Select(_ => RaceOneAsync(async () =>
            {
                await ready.Task;
                return await CallApproveAsync(resource, bookingId, approverId);
            }))
            .ToList();

        await Task.Delay(200);
        ready.SetResult();

        return await Task.WhenAll(racers);
    }

    // Opens its own connection ahead of the gate, exactly as
    // CreateBookingProcedureTests.RaceAsync does — so the race is over the
    // lock, not over connection setup.
    private static async Task<ProcedureOutcome> RaceOneAsync(Func<Task<ProcedureOutcome>> work) => await work();

    // ---- Calling the procedure -----------------------------------------------

    private async Task<ProcedureOutcome> CallApproveAsync(
        TestResource resource, Guid bookingId, Guid approverUserId, DateTime? nowUtc = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SetTenantContextAsync(connection, resource.OrgId);

        return await ExecuteApproveAsync(connection, bookingId, approverUserId, nowUtc ?? At(0));
    }

    // Retries a deadlock victim, because production does (IUnitOfWork). See
    // CreateBookingProcedureTests' identical helper for the full reasoning —
    // decision 0023 measured 1205 as routine at contention, not exotic.
    private static async Task<ProcedureOutcome> ExecuteApproveAsync(
        SqlConnection connection, Guid bookingId, Guid approverUserId, DateTime nowUtc)
    {
        const int deadlockVictim = 1205;
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteApproveOnceAsync(connection, bookingId, approverUserId, nowUtc);
            }
            catch (SqlException e) when (e.Number == deadlockVictim && attempt < maxAttempts)
            {
                await Task.Delay(attempt * 20);
            }
        }
    }

    private static async Task<ProcedureOutcome> ExecuteApproveOnceAsync(
        SqlConnection connection, Guid bookingId, Guid approverUserId, DateTime nowUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "dbo.ApproveBooking";
        command.CommandType = System.Data.CommandType.StoredProcedure;

        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ApproverUserId", approverUserId);
        command.Parameters.AddWithValue("@NowUtc", nowUtc);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "dbo.ApproveBooking returned no result row.");

        return new ProcedureOutcome(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private sealed record ProcedureOutcome(string ResultCode, int? RemainingCapacity);

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

    // ---- Arrangement, via the real API where the app can reach the state ---

    private sealed record TestResource(Guid Id, Guid OrgId, Guid MemberUserId);

    private async Task<TestResource> CreateResourceAsync(int capacity)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Approve Procedure Test {Guid.NewGuid():N}",
                description = "Created by the ApproveBooking procedure tests",
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

    // Through the real procedure, exactly as CreateBookingProcedureTests calls
    // it directly rather than through the endpoint — no endpoint exists yet.
    private async Task<Guid> CreateBookingAsync(
        TestResource resource, DateTime startsAtUtc, DateTime endsAtUtc, int quantity, string status)
    {
        var bookingId = Guid.NewGuid();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SetTenantContextAsync(connection, resource.OrgId);

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
        command.Parameters.AddWithValue("@NowUtc", At(0));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Created", reader.GetString(0));

        return bookingId;
    }

    private static async Task<Guid> OrgIdOfAsync(Guid resourceId) =>
        await ScalarAsync<Guid>("SELECT OrgId FROM dbo.Resources WHERE Id = @p0;", resourceId);

    private static async Task<Guid> MemberIdAsync() =>
        await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeMember);

    private static async Task<Guid> ApproverIdAsync() =>
        await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeApprover);

    private static async Task<int> CountConfirmedAsync(Guid resourceId) =>
        await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Bookings WHERE ResourceId = @p0 AND Status = 'Confirmed';", resourceId);

    private sealed record StoredBooking(string Status, Guid? UpdatedByUserId, DateTime UpdatedAtUtc);

    private static async Task<StoredBooking> ReadBookingAsync(Guid bookingId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, UpdatedByUserId, UpdatedAtUtc FROM dbo.Bookings WHERE Id = @BookingId;";
        command.Parameters.AddWithValue("@BookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");

        return new StoredBooking(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }

    // Decision 0017's carve-out: a state the API cannot produce on its own
    // (an over-capacity resource, or a non-Pending status reached without the
    // cancel/reject endpoints this phase has not built yet) is arranged with
    // raw SQL, never through LINQ or SaveChanges.
    private static async Task InsertBookingDirectlyAsync(
        TestResource resource, DateTime startsAtUtc, DateTime endsAtUtc, int quantity, string status)
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

    private static async Task SetStatusDirectlyAsync(Guid bookingId, string status)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.Bookings SET Status = @Status WHERE Id = @BookingId;";
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@BookingId", bookingId);
        await command.ExecuteNonQueryAsync();
    }

    // The conditional shape EF's own cancel issues (Booking.Cancel + a plain
    // SaveChangesAsync) — a raw stand-in for it here, since this file speaks
    // ADO throughout and the point is the *row-level lock contention*, not
    // EF's own concurrency token.
    private static async Task<int> CancelIfPendingDirectlyAsync(Guid bookingId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dbo.Bookings SET Status = 'Cancelled' WHERE Id = @BookingId AND Status = 'Pending';";
        command.Parameters.AddWithValue("@BookingId", bookingId);

        return await command.ExecuteNonQueryAsync();
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
