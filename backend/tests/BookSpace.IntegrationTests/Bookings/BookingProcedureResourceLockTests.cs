using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;

namespace BookSpace.IntegrationTests.Bookings;

// Hardening pass, items 3 and 4: dbo.CreateBooking and dbo.ApproveBooking now
// take WITH (HOLDLOCK) on the Resources/ResourceApprovers rows they read to
// decide, instead of reading them unlocked (see the two "...LocksResource..."
// migrations for the reasoning). These tests prove the lock actually blocks a
// conflicting writer, rather than re-proving capacity correctness — the
// capacity race is already covered exhaustively by CreateBookingProcedureTests
// and ApproveBookingProcedureTests.
//
// **Deterministic, not a hoped-for interleave.** A real race between a booking
// call and an admin's Resources write is too narrow a window to force
// reliably by firing both at once and hoping. Instead: dbo.CreateBooking /
// dbo.ApproveBooking is called from *within a transaction the test itself
// opened* (the same nested-transaction support IUnitOfWork's callers already
// rely on — the procedure detects @@TRANCOUNT <> 0 and skips its own commit),
// so the lock stays held after the call returns. A concurrent conflicting
// write from a second connection is started and asserted *not yet complete*
// after a short bounded wait — proving it is blocked, not racing to see who
// wins — then the first transaction is rolled back and the second write is
// awaited to completion, proving the lock was the only thing in its way.
//
// The bounded wait is a "did this NOT finish yet" check, not a coordination
// sleep between two independent operations — CLAUDE.md §8's preference for
// deterministic concurrency tests is about the latter.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingProcedureResourceLockTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeApprover = "approver@acme.test";

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    private static readonly DateTime StartsAtUtc = new(2027, 4, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndsAtUtc = new(2027, 4, 12, 10, 0, 0, DateTimeKind.Utc);

    // Long enough that a genuinely-unblocked statement always finishes well
    // within it (this suite's own procedure calls elsewhere complete in low
    // milliseconds); short enough that a real regression fails fast rather
    // than hanging the run.
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(2);

    public BookingProcedureResourceLockTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task CreateBooking_HoldsTheResourceRowLocked_BlockingAConcurrentArchive()
    {
        var resource = await CreateResourceAsync(capacity: 4);

        await using var holderConnection = new SqlConnection(ConnectionString);
        await holderConnection.OpenAsync();
        await SetTenantContextAsync(holderConnection, resource.OrgId);
        await using var holderTransaction = (SqlTransaction)await holderConnection.BeginTransactionAsync();

        try
        {
            var bookingId = Guid.NewGuid();
            var outcome = await CreateBookingOnAsync(
                holderConnection, holderTransaction, resource, bookingId, "Confirmed");
            Assert.Equal("Created", outcome);

            // The holder's transaction is still open — dbo.CreateBooking saw
            // @@TRANCOUNT <> 0 and skipped its own COMMIT — so the HOLDLOCK it
            // took on this Resources row is still held.
            var archiveTask = ArchiveResourceAsync(resource.Id);

            var finishedInTime = await Task.WhenAny(archiveTask, Task.Delay(BoundedWait)) == archiveTask;
            Assert.False(finishedInTime, "The archive UPDATE should have blocked on the still-open booking transaction.");

            await holderTransaction.RollbackAsync();

            // Released now — the archive can proceed and must actually land.
            await archiveTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await IsArchivedAsync(resource.Id));
        }
        finally
        {
            await CleanUpResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task ApproveBooking_HoldsTheApproverRowLocked_BlockingAConcurrentRevocation()
    {
        var resource = await CreateResourceAsync(capacity: 4);
        var approverId = await ApproverIdAsync();
        await AssignApproverAsync(resource, approverId);
        var bookingId = await CreatePendingBookingAsync(resource);

        await using var holderConnection = new SqlConnection(ConnectionString);
        await holderConnection.OpenAsync();
        await SetTenantContextAsync(holderConnection, resource.OrgId);
        await using var holderTransaction = (SqlTransaction)await holderConnection.BeginTransactionAsync();

        try
        {
            var outcome = await ApproveBookingOnAsync(holderConnection, holderTransaction, bookingId, approverId);
            Assert.Equal("Approved", outcome);

            // Still open — the HOLDLOCK this procedure took on the
            // ResourceApprovers row for (resource, approver) is still held.
            var revokeTask = RevokeApproverAsync(resource.Id, approverId);

            var finishedInTime = await Task.WhenAny(revokeTask, Task.Delay(BoundedWait)) == revokeTask;
            Assert.False(finishedInTime, "The DELETE from ResourceApprovers should have blocked on the still-open approval transaction.");

            await holderTransaction.RollbackAsync();

            await revokeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await IsAssignedApproverAsync(resource.Id, approverId));
        }
        finally
        {
            await CleanUpBookingAsync(bookingId);
            await CleanUpResourceAsync(resource.Id);
        }
    }

    // ---- Calling the procedures inside the test's own open transaction -----

    private static async Task<string> CreateBookingOnAsync(
        SqlConnection connection, SqlTransaction transaction, TestResource resource, Guid bookingId, string status)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "dbo.CreateBooking";
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ResourceId", resource.Id);
        command.Parameters.AddWithValue("@UserId", resource.MemberUserId);
        command.Parameters.AddWithValue("@StartsAtUtc", StartsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", EndsAtUtc);
        command.Parameters.AddWithValue("@Quantity", 1);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@CreatedByUserId", resource.MemberUserId);
        command.Parameters.AddWithValue("@NowUtc", StartsAtUtc.AddHours(-1));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetString(0);
    }

    private static async Task<string> ApproveBookingOnAsync(
        SqlConnection connection, SqlTransaction transaction, Guid bookingId, Guid approverId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "dbo.ApproveBooking";
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ApproverUserId", approverId);
        command.Parameters.AddWithValue("@NowUtc", StartsAtUtc.AddHours(-1));
        command.Parameters.AddWithValue("@CallerIsTenantAdmin", false);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetString(0);
    }

    // ---- The concurrent writes under test ------------------------------------

    private static async Task ArchiveResourceAsync(Guid resourceId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.Resources SET IsArchived = 1 WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", resourceId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeApproverAsync(Guid resourceId, Guid approverId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.ResourceApprovers WHERE ResourceId = @resourceId AND UserId = @userId;";
        command.Parameters.AddWithValue("@resourceId", resourceId);
        command.Parameters.AddWithValue("@userId", approverId);
        await command.ExecuteNonQueryAsync();
    }

    // ---- Inspection -----------------------------------------------------------

    private static async Task<bool> IsArchivedAsync(Guid resourceId) =>
        await ScalarAsync<bool>("SELECT IsArchived FROM dbo.Resources WHERE Id = @p0;", resourceId);

    private static async Task<bool> IsAssignedApproverAsync(Guid resourceId, Guid approverId) =>
        await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ResourceApprovers WHERE ResourceId = @p0 AND UserId = @p1;",
            resourceId, approverId) > 0;

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

    // ---- Session context ------------------------------------------------------

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

    private static async Task EnterRlsBypassAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'TenantInit',   @value = 1;
            EXEC sp_set_session_context @key = N'TenantBypass', @value = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    // ---- Arrangement, via the real API where the app can reach the state ----

    private sealed record TestResource(Guid Id, Guid OrgId, Guid MemberUserId);

    private async Task<TestResource> CreateResourceAsync(int capacity)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Lock Test {Guid.NewGuid():N}",
                description = "Created by BookingProcedureResourceLockTests",
                resourceType = "Room",
                capacity,
                timeZoneId = "UTC",
                requiresApproval = false,
                minDurationMinutes = (int?)null,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;

        var orgId = await ScalarAsync<Guid>("SELECT OrgId FROM dbo.Resources WHERE Id = @p0;", created.Id);
        var memberId = await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", "member1@acme.test");

        return new TestResource(created.Id, orgId, memberId);
    }

    private async Task AssignApproverAsync(TestResource resource, Guid approverId)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        (await client.PutAsJsonAsync(
            $"/resources/{resource.Id}/approvers",
            new { approverUserIds = new[] { approverId } })).EnsureSuccessStatusCode();
    }

    private async Task<Guid> CreatePendingBookingAsync(TestResource resource)
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
        command.Parameters.AddWithValue("@StartsAtUtc", StartsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", EndsAtUtc);
        command.Parameters.AddWithValue("@Quantity", 1);
        command.Parameters.AddWithValue("@Status", "Pending");
        command.Parameters.AddWithValue("@CreatedByUserId", resource.MemberUserId);
        command.Parameters.AddWithValue("@NowUtc", StartsAtUtc.AddHours(-1));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Created", reader.GetString(0));

        return bookingId;
    }

    private static async Task<Guid> ApproverIdAsync() =>
        await ScalarAsync<Guid>("SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeApprover);

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

    // ---- Teardown ---------------------------------------------------------

    private static async Task CleanUpBookingAsync(Guid bookingId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.Bookings WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", bookingId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanUpResourceAsync(Guid resourceId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.Resources WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", resourceId);
        await command.ExecuteNonQueryAsync();
    }
}
