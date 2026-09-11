using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Bookings.ApproveBooking;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.RejectBooking;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// WP-5 Phase 3: POST /bookings/{id}/approve and .../reject through the real
// pipeline — real middleware, real JwtBearer handler, real authorization
// policies, real SQL Server. The lock itself is proved in
// ApproveBookingProcedureTests; what this file adds is the pipeline around
// it — reach resolution (decision 0018/0002 reapplied), the reason-code
// mapping, and the notification side effect — mirroring the split
// CreateBookingProcedureTests/CreateBookingEndpointTests already established.
//
// **No 409 test here.** Same reasoning ApproveBookingProcedureTests' header
// gives: a Pending booking already reserves its units in full (decision
// 0005), so no legitimate HTTP sequence can put dbo.ApproveBooking's capacity
// re-check in a position to refuse. That state is only reachable with raw
// SQL, and it is already covered at the procedure level.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingApprovalEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    public BookingApprovalEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 5, 13, hour, minute, 0, DateTimeKind.Utc);

    // ---- Approve: the happy path (FR-7.1-7.4) --------------------------------

    [Fact]
    public async Task Approve_LetsTheTenantAdminConfirmAPendingBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));
            Assert.Equal(BookingStatus.Pending, created.Status);

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsJsonAsync(
                $"/bookings/{created.Id}/approve", new { note = "Looks fine" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<ApproveBookingCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(created.Id, body.Id);
            Assert.Equal(BookingStatus.Confirmed, body.Status);
            Assert.NotEqual(created.UserId, body.DecidedByUserId);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Confirmed';",
                created.Id));
            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.ApprovalRequests
                WHERE BookingId = @p0 AND Decision = 'Approved' AND Note = 'Looks fine';
                """,
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Decision 0018: an assigned Approver may decide without being a
    // TenantAdmin at all.
    [Fact]
    public async Task Approve_LetsTheAssignedApproverConfirmIt()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var response = await approver.PostAsync($"/bookings/{created.Id}/approve", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var detail = (await (await member.GetAsync($"/bookings/{created.Id}"))
                .Content.ReadFromJsonAsync<GetBookingQueryResponse>(TestJson.Options))!;
            Assert.Equal(BookingStatus.Confirmed, detail.Status);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Approve_EnqueuesAConfirmationNotificationForTheBooker()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/bookings/{created.Id}/approve", null)).EnsureSuccessStatusCode();

            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Notifications
                WHERE BookingId = @p0 AND Kind = 'Confirmed' AND RecipientUserId = @p1;
                """,
                created.Id, created.UserId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Approve: who may reach the booking (decision 0018/0002) ------------

    // A plain Member has neither reach — not the admin's tenant-wide sweep,
    // not an approver's own-resource set — so even the booking's own owner
    // gets 404, never a 403 that would confirm it exists.
    [Fact]
    public async Task Approve_RefusesAPlainMemberEvenOverTheirOwnBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var response = await member.PostAsync($"/bookings/{created.Id}/approve", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // AC-4: another tenant's TenantAdmin has no reach into Acme at all.
    [Fact]
    public async Task Approve_RefusesAnAdminFromAnotherTenant()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var globexAdmin = await AuthenticatedClientAsync(GlobexAdmin);
            var response = await globexAdmin.PostAsync($"/bookings/{created.Id}/approve", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Approve_RefusesANonexistentBooking()
    {
        var response = await (await AuthenticatedClientAsync(AcmeAdmin))
            .PostAsync($"/bookings/{Guid.NewGuid()}/approve", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));
    }

    // ---- Approve: state refusals (closing WP-4's loose end 1) ---------------

    [Fact]
    public async Task Approve_RefusesABookingThatIsAlreadyConfirmed()
    {
        // No approval gate at all, so the create itself lands Confirmed.
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));
            Assert.Equal(BookingStatus.Confirmed, created.Status);

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsync($"/bookings/{created.Id}/approve", null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingNotPending", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Approve_RefusesABookingAlreadyDecidedByAnotherApprover()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/bookings/{created.Id}/approve", null)).EnsureSuccessStatusCode();

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var response = await approver.PostAsync($"/bookings/{created.Id}/approve", null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingNotPending", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Approve_RefusesACancelledBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));
            (await member.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsync($"/bookings/{created.Id}/approve", null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingNotPending", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Validation -----------------------------------------------------------

    [Fact]
    public async Task Approve_RejectsAnOverLongNote()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsJsonAsync(
                $"/bookings/{created.Id}/approve",
                new { note = new string('a', ApproveBookingCommandRequestValidator.MaxNoteLength + 1) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));

            // Refused before the handler ever ran — still Pending.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Pending';", created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Reject: the happy path (FR-7.1-7.4) --------------------------------

    [Fact]
    public async Task Reject_LetsTheTenantAdminRefuseAPendingBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsJsonAsync(
                $"/bookings/{created.Id}/reject", new { note = "Room needed elsewhere" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<RejectBookingCommandResponse>(
                TestJson.Options))!;
            Assert.Equal(BookingStatus.Rejected, body.Status);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Rejected';", created.Id));
            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.ApprovalRequests
                WHERE BookingId = @p0 AND Decision = 'Rejected' AND Note = 'Room needed elsewhere';
                """,
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Reject_LetsTheAssignedApproverRefuseIt()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var response = await approver.PostAsync($"/bookings/{created.Id}/reject", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Reject_EnqueuesARejectionNotificationForTheBooker()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/bookings/{created.Id}/reject", null)).EnsureSuccessStatusCode();

            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Notifications
                WHERE BookingId = @p0 AND Kind = 'Rejected' AND RecipientUserId = @p1;
                """,
                created.Id, created.UserId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Reject: reach and state, mirroring approve --------------------------

    [Fact]
    public async Task Reject_RefusesAPlainMemberEvenOverTheirOwnBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var response = await member.PostAsync($"/bookings/{created.Id}/reject", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Reject_RefusesAnAdminFromAnotherTenant()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var globexAdmin = await AuthenticatedClientAsync(GlobexAdmin);
            var response = await globexAdmin.PostAsync($"/bookings/{created.Id}/reject", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Reject_RefusesABookingThatIsAlreadyConfirmed()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsync($"/bookings/{created.Id}/reject", null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingNotPending", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Rejecting removes a claim rather than adding one, so the slot is free
    // immediately — asserted the same way BookingCancelEndpointTests proves a
    // cancel frees a slot.
    [Fact]
    public async Task Reject_FreesTheSlotForANewBooking()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1, requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var first = await CreateBookingAsync(member, resource, At(9), At(10));
            Assert.Equal(BookingStatus.Pending, first.Status);

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/bookings/{first.Id}/reject", null)).EnsureSuccessStatusCode();

            var second = await CreateBookingAsync(member, resource, At(9), At(10));
            Assert.Equal(BookingStatus.Pending, second.Status);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Reject_RejectsAnOverLongNote()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsJsonAsync(
                $"/bookings/{created.Id}/reject",
                new { note = new string('a', RejectBookingCommandRequestValidator.MaxNoteLength + 1) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
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
        HttpClient client, Guid resourceId, DateTime start, DateTime end, int quantity = 1)
    {
        var response = await client.PostAsJsonAsync("/bookings", Booking(resourceId, start, end, quantity));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(TestJson.Options))!;
    }

    private async Task<Guid> CreateBookableResourceAsync(
        int capacity = 4, bool requiresApproval = false, string admin = AcmeAdmin)
    {
        var client = await AuthenticatedClientAsync(admin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Booking Approval Test {Guid.NewGuid():N}",
                description = "Created by the booking approval endpoint tests",
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
                    description = "Created by the booking approval endpoint tests",
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
