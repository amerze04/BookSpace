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

// WP-4 Phase 1c: POST /bookings through the real pipeline — real middleware,
// real JwtBearer handler, real authorization policies, real SQL Server.
//
// This file settles the work package's first three Week-3 tasks and two of its
// acceptance criteria: a member creates a one-off booking, every rule violation
// is refused with a machine-readable reason, and the reason-code table below is
// the "clear, structured errors" evidence (FR-4.5).
//
// The concurrency criterion (AC-1) is **not** here. It is proved in
// CreateBookingProcedureTests, at the layer where the lock actually is; the
// HTTP-level race is BookingConcurrencyEndpointTests (Phase 3), where what it
// adds is the pipeline rather than the guarantee.
//
// **Resources are in the UTC zone** unless a test is about zones, for the reason
// the availability tests give: the conversion rules have their own thorough
// tests, and a resource whose local time is UTC keeps these assertions about the
// endpoint rather than about arithmetic the test would have to redo.
//
// Same state hygiene as its siblings: every test creates its own resource and
// removes it again, since the host's database is shared across the collection
// and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class CreateBookingEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public CreateBookingEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // A fixed future Thursday in whole seconds — datetime2(0) rounds on write
    // (CLAUDE.md §4.3), and the validator refuses fractions anyway.
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 3, 11, hour, minute, 0, DateTimeKind.Utc);

    private static object Booking(Guid resourceId, DateTime start, DateTime end, int quantity = 1) =>
        new
        {
            resourceId,
            startsAtUtc = start.ToString("o"),
            endsAtUtc = end.ToString("o"),
            quantity,
            title = "Design review",
        };

    // ---- The happy path (FR-4.1) -------------------------------------------

    [Fact]
    public async Task Post_CreatesAConfirmedBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
                TestJson.Options))!;

            Assert.NotEqual(Guid.Empty, body.Id);
            Assert.Equal(resource, body.ResourceId);
            Assert.Equal(At(9), body.StartsAtUtc);
            Assert.Equal(At(10), body.EndsAtUtc);
            Assert.Equal(1, body.Quantity);
            Assert.Equal("Design review", body.Title);
            Assert.Null(body.Approval);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Confirmed';", body.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The status serializes as its name, not its ordinal — Program.cs registered
    // JsonStringEnumConverter app-wide in WP-3 Phase 3, and BookingStatus is the
    // payoff that entry predicted.
    [Fact]
    public async Task Post_SerializesTheStatusAsItsName()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The instants come back with their Z, so a browser reads them as UTC rather
    // than as local time — the §4.3 Kind convention, end to end.
    [Fact]
    public async Task Post_ReturnsInstantsWithAUtcDesignator()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.EndsWith("Z", body.GetProperty("startsAtUtc").GetString());
            Assert.EndsWith("Z", body.GetProperty("createdAtUtc").GetString());
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // FR-8.1: the confirmation row the dispatch job will later send. Nothing
    // sends anything yet (CLAUDE.md §7) — the row and UQ_Notifications_Once are
    // what make the eventual send idempotent.
    [Fact]
    public async Task Post_EnqueuesAConfirmationNotification()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));
            var body = (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Notifications
                WHERE BookingId = @p0 AND Kind = 'Confirmed' AND SentAtUtc IS NULL;
                """,
                body.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // A booking consumes availability the moment it exists: the same interval is
    // no longer offered by the query the member picked it from. This is the two
    // halves of the system agreeing, which is what putting the calculation in
    // Domain was for.
    [Fact]
    public async Task Post_ConsumesTheAvailabilityTheQueryOffered()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var before = await BookableIntervalCountAsync(client, resource);
            Assert.Equal(1, before);

            (await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10))))
                .EnsureSuccessStatusCode();

            // The day is split either side of the booking, so the count rises
            // rather than falling — what matters is that the booked hour is gone.
            var after = await BookableIntervalsAsync(client, resource);
            Assert.DoesNotContain(after, i => i.StartUtc < At(10) && At(9) < i.EndUtc);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Approval routing (FR-7.1) -----------------------------------------

    [Fact]
    public async Task Post_OnAnApprovalGatedResource_CreatesAPendingBookingAndItsRequest()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
                TestJson.Options))!;

            Assert.Equal("Pending", body.Status.ToString());
            Assert.NotNull(body.Approval);
            Assert.NotEqual(Guid.Empty, body.Approval!.ApprovalRequestId);

            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.ApprovalRequests
                WHERE BookingId = @p0 AND Decision = 'Pending';
                """,
                body.Id));

            // One per approver, and none to the booker: there is nothing to
            // confirm yet, and FR-7.3 puts the member's notice at the decision.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Notifications WHERE BookingId = @p0 AND Kind = 'ApprovalRequested';",
                body.Id));
            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Notifications WHERE BookingId = @p0 AND Kind = 'Confirmed';",
                body.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // A Pending booking holds its units, which is why creating one is safe rather
    // than a way to oversubscribe an approval-gated resource.
    [Fact]
    public async Task Post_APendingBookingStillConsumesCapacity()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1, requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            (await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10))))
                .EnsureSuccessStatusCode();

            var second = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("SlotUnavailable", await ReasonCodeAsync(second));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Structured errors (FR-4.5) ----------------------------------------

    // Every reason code this endpoint can raise, against the status its ErrorKind
    // promises (decision 0016). The same table the WP-3 endpoints keep, extended
    // to the create path — a new failure has to appear here or it is not
    // contracted.
    [Fact]
    public async Task Post_RefusesAnUnknownResourceWith404()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.PostAsJsonAsync("/bookings", Booking(Guid.NewGuid(), At(9), At(10)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ResourceNotFound", await ReasonCodeAsync(response));
    }

    // AC-4: another tenant's real resource id is byte-identical to one that
    // exists nowhere, aside from the per-request correlation id.
    [Fact]
    public async Task Post_AnotherTenantsResourceIsIndistinguishableFromAMissingOne()
    {
        var globex = await CreateBookableResourceAsync(admin: GlobexAdmin);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var crossTenant = await client.PostAsJsonAsync("/bookings", Booking(globex, At(9), At(10)));
            var nonexistent = await client.PostAsJsonAsync("/bookings", Booking(Guid.NewGuid(), At(9), At(10)));

            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            Assert.Equal(await ComparableBodyAsync(nonexistent), await ComparableBodyAsync(crossTenant));
        }
        finally
        {
            await CleanUpAsync(globex, admin: GlobexAdmin);
        }
    }

    [Fact]
    public async Task Post_RefusesAnArchivedResourceWith422()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/resources/{resource}/archive", null)).EnsureSuccessStatusCode();

            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("ResourceArchived", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_RefusesAnIntervalOutsideAvailabilityWith422()
    {
        // Open 09:00-17:00 only, so the evening is outside.
        var resource = await CreateBookableResourceAsync(opensAt: "09:00:00", closesAt: "17:00:00");

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(19), At(20)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("OutsideAvailability", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Partly open is not open: a booking running past closing is refused, never
    // truncated.
    [Fact]
    public async Task Post_RefusesAnIntervalRunningPastClosingWith422()
    {
        var resource = await CreateBookableResourceAsync(opensAt: "09:00:00", closesAt: "17:00:00");

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(16), At(18)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("OutsideAvailability", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_RefusesAnIntervalInsideABlackoutWith422()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsJsonAsync(
                $"/resources/{resource}/blackout-periods",
                new
                {
                    startsAtUtc = At(8).ToString("o"),
                    endsAtUtc = At(12).ToString("o"),
                    reason = "Boiler service",
                })).EnsureSuccessStatusCode();

            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BlackoutPeriod", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_RefusesATakenSlotWith409()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            (await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10))))
                .EnsureSuccessStatusCode();

            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("SlotUnavailable", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The other half of the 2026-09-07 split: units are free, just not enough.
    [Fact]
    public async Task Post_RefusesTooManyUnitsWith409CapacityExceeded()
    {
        var resource = await CreateBookableResourceAsync(capacity: 4);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            (await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10), quantity: 3)))
                .EnsureSuccessStatusCode();

            var response = await client.PostAsJsonAsync(
                "/bookings", Booking(resource, At(9), At(10), quantity: 2));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("CapacityExceeded", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_RefusesADurationOutsideTheResourceLimitsWith422()
    {
        var resource = await CreateBookableResourceAsync(minDurationMinutes: 60);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync(
                "/bookings", Booking(resource, At(9), At(9).AddMinutes(15)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingDurationOutOfRange", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_RefusesAnIntervalWhollyInThePastWith422()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var yesterday = DateTime.UtcNow.Date.AddDays(-1).AddHours(9);
            var response = await client.PostAsJsonAsync(
                "/bookings",
                Booking(
                    resource,
                    DateTime.SpecifyKind(yesterday, DateTimeKind.Utc),
                    DateTime.SpecifyKind(yesterday.AddHours(1), DateTimeKind.Utc)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingInThePast", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Malformed rather than refused: a 400 with per-field errors, and the handler
    // is never reached (ValidationBehavior short-circuits the pipeline).
    [Theory]
    [InlineData(0)]   // quantity below the CHECK constraint
    [InlineData(-2)]
    public async Task Post_RefusesAMalformedQuantityWith400(int quantity)
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync(
                "/bookings", Booking(resource, At(9), At(10), quantity));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            // Read once: the content stream is forward-only, so asking for the
            // reason code and then the body again would fail on a closed stream.
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());
            Assert.True(body.TryGetProperty("errors", out _));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Post_RefusesAnInvertedIntervalWith400()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(10), At(9)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Every error body is a ProblemDetails carrying the correlation id, and the
    // exception's message never reaches the client (decision 0016).
    [Fact]
    public async Task Post_ErrorBodiesCarryACorrelationIdAndNoInternalMessage()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);
        var resourceId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/bookings", Booking(resourceId, At(9), At(10)));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("correlationId", out var correlationId));
        Assert.False(string.IsNullOrWhiteSpace(correlationId.GetString()));

        // The log-only message names the resource id; the response must not.
        Assert.DoesNotContain(resourceId.ToString(), body.GetRawText());
    }

    // ---- Authorization -----------------------------------------------------

    [Fact]
    public async Task Post_IsRefusedWithoutAToken()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/bookings", Booking(Guid.NewGuid(), At(9), At(10)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A SysAdmin has no orgId claim (decision 0012), so TenantMember excludes
    // them: the Platform Operator must never book into a tenant.
    [Fact]
    public async Task Post_IsForbiddenToASysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsJsonAsync("/bookings", Booking(Guid.NewGuid(), At(9), At(10)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Booking is a member's flow, so every tenant role may do it — the contrast
    // with the resource write endpoints, which are TenantAdmin only.
    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    [InlineData(AcmeAdmin)]
    public async Task Post_IsAllowedToEveryTenantRole(string email)
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(email);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The booking is owned by the caller, taken from the token — there is no
    // userId on the wire to forge.
    [Fact]
    public async Task Post_RecordsTheCallerAsTheOwner()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));
            var body = (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
                TestJson.Options))!;

            var memberId = await ScalarAsync<Guid>(
                "SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeMember);

            Assert.Equal(memberId, body.UserId);
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND UserId = @p1;", body.Id, memberId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private async Task<Guid> CreateBookableResourceAsync(
        int capacity = 4,
        bool requiresApproval = false,
        int? minDurationMinutes = null,
        string opensAt = "00:00:00",
        string closesAt = "23:59:59",
        string admin = AcmeAdmin)
    {
        var client = await AuthenticatedClientAsync(admin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Booking Test {Guid.NewGuid():N}",
                description = "Created by the booking endpoint tests",
                resourceType = "Room",
                capacity,
                timeZoneId = "UTC",
                requiresApproval = false,
                minDurationMinutes,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();

        var created = (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(
            TestJson.Options))!;

        // Open every weekday. 23:59:59 is this system's spelling of midnight
        // (decision 0022), so the default makes the day continuous.
        var windows = Enum.GetValues<DayOfWeek>()
            .Select(day => new { weekday = day.ToString(), opensAt, closesAt })
            .ToArray();

        (await client.PutAsJsonAsync(
            $"/resources/{created.Id}/availability-windows", new { windows })).EnsureSuccessStatusCode();

        if (requiresApproval)
        {
            // Approvers first, then the flag: RequiresApproval with an empty
            // approver list is refused at both ends (ApproversRequired, WP-3
            // Phase 3).
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
                    description = "Created by the booking endpoint tests",
                    resourceType = "Room",
                    capacity,
                    timeZoneId = "UTC",
                    requiresApproval = true,
                    minDurationMinutes,
                    maxDurationMinutes = (int?)null,
                })).EnsureSuccessStatusCode();
        }

        return created.Id;
    }

    private sealed record Interval(DateTime StartUtc, DateTime EndUtc);

    private static async Task<IReadOnlyList<Interval>> BookableIntervalsAsync(HttpClient client, Guid resourceId)
    {
        var date = DateOnly.FromDateTime(At(9));
        var response = await client.GetAsync(
            $"/resources/{resourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("intervals").EnumerateArray()
            .Select(i => new Interval(
                i.GetProperty("startUtc").GetDateTime(),
                i.GetProperty("endUtc").GetDateTime()))
            .ToList();
    }

    private static async Task<int> BookableIntervalCountAsync(HttpClient client, Guid resourceId) =>
        (await BookableIntervalsAsync(client, resourceId)).Count;

    private static async Task<string?> ReasonCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.TryGetProperty("reasonCode", out var code) ? code.GetString() : null;
    }

    // Everything except the per-request fields, so two error bodies can be
    // compared for indistinguishability (AC-4).
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

    // Notifications and approval requests reference Bookings; Bookings reference
    // Resources with NoAction (CLAUDE.md §4.5), so the order matters.
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
