using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.UpdateResource;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// WP-3 Phase 2 step 3: POST /resources and PUT /resources/{id} through the real
// pipeline. The rule checks themselves are unit-tested against fakes; what these
// prove is the parts a fake cannot — that the TenantAdmin policy is actually
// wired, that each reason code arrives with the status ErrorKind promises, and
// that a real INSERT/UPDATE gets past the tenant guard, RLS and the CHECK
// constraints.
//
// State hygiene: every test that writes creates its own resource and removes it
// again. The host and its database are shared across the collection, and other
// tests — TenantIsolationTests, and the read tests next door — assert on Acme's
// exact resource count, so a row left behind would break them from a distance.
[Collection(nameof(AuthenticationTestCollection))]
public class ResourceWriteEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public ResourceWriteEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static object ValidPayload(
        string name = "Integration Test Room",
        string? description = "Created by a test",
        string resourceType = "Room",
        int capacity = 4,
        string timeZoneId = "America/New_York",
        bool requiresApproval = false,
        int? min = 30,
        int? max = 240) =>
        new
        {
            name,
            description,
            resourceType,
            capacity,
            timeZoneId,
            requiresApproval,
            minDurationMinutes = min,
            maxDurationMinutes = max,
        };

    // ---- POST: the happy path ----

    [Fact]
    public async Task Create_AsTenantAdmin_Returns201WithALocationHeaderAndIsThenReadable()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        Guid createdId = Guid.Empty;

        try
        {
            var response = await client.PostAsJsonAsync("/resources", ValidPayload(name: "Created Room"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var created = await response.Content.ReadFromJsonAsync<ResourceDetailResponse>();
            createdId = created!.Id;

            Assert.NotEqual(Guid.Empty, created.Id);
            Assert.Equal("Created Room", created.Name);
            Assert.Equal("Created by a test", created.Description);
            Assert.Equal("Room", created.ResourceType);
            Assert.Equal(4, created.Capacity);
            Assert.Equal("America/New_York", created.TimeZoneId);
            Assert.False(created.RequiresApproval);
            Assert.False(created.IsArchived);

            // The Location header points at the read endpoint, so a client never
            // has to construct the URL itself. CreatedAtAction emits an absolute
            // URI, hence AbsolutePath rather than a whole-string comparison.
            Assert.Equal($"/resources/{created.Id}", response.Headers.Location!.AbsolutePath);

            var fetched = await client.GetFromJsonAsync<ResourceDetailResponse>(
                response.Headers.Location.AbsolutePath);
            Assert.Equal(created, fetched);
        }
        finally
        {
            await DeleteResourceAsync(createdId);
        }
    }

    // OrgId is never on the wire: it comes from the token, so a created resource
    // lands in the caller's own tenant and nowhere else.
    [Fact]
    public async Task Create_StampsTheCallersOwnTenant()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var acmeOrgId = await GetOrgIdAsync("acme");
        Guid createdId = Guid.Empty;

        try
        {
            var created = await PostAndReadAsync(client, ValidPayload(name: "Tenant Stamped Room"));
            createdId = created.Id;

            Assert.Equal(acmeOrgId, await ReadOrgIdOfResourceAsync(createdId));
        }
        finally
        {
            await DeleteResourceAsync(createdId);
        }
    }

    // Nulls are accepted where the column is nullable: "no limit" and "no
    // description" are legitimate configurations, not missing input.
    [Fact]
    public async Task Create_AcceptsNullDescriptionAndNullDurationLimits()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        Guid createdId = Guid.Empty;

        try
        {
            var created = await PostAndReadAsync(
                client, ValidPayload(name: "Sparse Room", description: null, min: null, max: null));
            createdId = created.Id;

            Assert.Null(created.Description);
            Assert.Null(created.MinDurationMinutes);
            Assert.Null(created.MaxDurationMinutes);
        }
        finally
        {
            await DeleteResourceAsync(createdId);
        }
    }

    // ---- POST: authorization (WP-3 AC "non-admins cannot create or edit") ----

    [Fact]
    public async Task Create_AsMember_IsForbidden()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.PostAsJsonAsync("/resources", ValidPayload());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A SysAdmin passes the TenantAdmin policy's role check but fails
    // TenantMember's orgId-claim requirement (decision 0012) — and has no tenant
    // to create a resource in anyway.
    [Fact]
    public async Task Create_AsSysAdmin_IsForbidden()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsJsonAsync("/resources", ValidPayload());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_Unauthenticated_IsUnauthorized()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/resources", ValidPayload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // 403 comes from the policy with no reason code at all, while a rule refusal
    // carries one — which is how you tell whether you are testing RBAC or the
    // domain (wp3-plan's "three behaviours that look like bugs").
    [Fact]
    public async Task Create_AsMember_IsRefusedByThePolicyBeforeAnyRuleRuns()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        // Would fail InvalidTimeZone if it ever reached the handler.
        var response = await client.PostAsJsonAsync("/resources", ValidPayload(timeZoneId: "Mars/Olympus"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("reasonCode", await response.Content.ReadAsStringAsync());
    }

    // ---- POST: shape validation (400, per-field) ----

    [Theory]
    [InlineData("name")]
    [InlineData("resourceType")]
    [InlineData("timeZoneId")]
    public async Task Create_WithABlankRequiredField_Returns400WithThatField(string field)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var payload = field switch
        {
            "name" => ValidPayload(name: "   "),
            "resourceType" => ValidPayload(resourceType: "   "),
            _ => ValidPayload(timeZoneId: "   "),
        };

        var response = await client.PostAsJsonAsync("/resources", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());
        var expectedProperty = char.ToUpperInvariant(field[0]) + field[1..];
        Assert.True(body.GetProperty("errors").TryGetProperty(expectedProperty, out _));
    }

    [Fact]
    public async Task Create_WithZeroCapacity_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync("/resources", ValidPayload(capacity: 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("Capacity", out _));
    }

    [Fact]
    public async Task Create_WithMaxDurationBelowMin_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync("/resources", ValidPayload(min: 120, max: 60));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("MaxDurationMinutes", out _));
    }

    // ---- POST: rule refusals (the first throwers for these codes) ----

    // ErrorKind.Validation, so 400 — the value is simply wrong, and the shape
    // validator could not have known the host's timezone list.
    [Fact]
    public async Task Create_WithAnUnknownTimeZone_Returns400InvalidTimeZone()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync("/resources", ValidPayload(timeZoneId: "Mars/Olympus"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReasonCodeAsync(response, "InvalidTimeZone");
    }

    // CLAUDE.md §4.3: a Windows zone id is refused even though TimeZoneInfo
    // resolves it, because the rest of the system reads this column as IANA.
    [Fact]
    public async Task Create_WithAWindowsTimeZoneId_Returns400InvalidTimeZone()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            "/resources", ValidPayload(timeZoneId: "Eastern Standard Time"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReasonCodeAsync(response, "InvalidTimeZone");
    }

    // ErrorKind.RuleViolation, so 422: the request was well-formed and a rule
    // refused it (FR-3.3). Approver assignment is Phase 3, so there is no way to
    // supply one here — which is exactly why creating in this state is refused
    // rather than allowed "temporarily".
    [Fact]
    public async Task Create_WithRequiresApprovalAndNoApprovers_Returns422ApproversRequired()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync("/resources", ValidPayload(requiresApproval: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertReasonCodeAsync(response, "ApproversRequired");
    }

    // ---- PUT ----

    [Fact]
    public async Task Update_AsTenantAdmin_ReplacesEveryFieldAndPersists()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Before Rename"));

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{created.Id}",
                ValidPayload(
                    name: "After Rename",
                    description: null,
                    resourceType: "MeetingRoom",
                    capacity: 10,
                    min: 15,
                    max: 60));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var updated = await response.Content.ReadFromJsonAsync<UpdateResourceResponse>();
            Assert.Equal("After Rename", updated!.Resource.Name);
            // Full representation: an omitted nullable field clears it.
            Assert.Null(updated.Resource.Description);
            Assert.Equal("MeetingRoom", updated.Resource.ResourceType);
            Assert.Equal(10, updated.Resource.Capacity);
            Assert.Equal(15, updated.Resource.MinDurationMinutes);
            Assert.Equal(60, updated.Resource.MaxDurationMinutes);
            Assert.Null(updated.TimeZoneChange);
            Assert.True(updated.Resource.UpdatedAtUtc >= created.UpdatedAtUtc);

            var fetched = await client.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{created.Id}");
            Assert.Equal("After Rename", fetched!.Name);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    [Fact]
    public async Task Update_AsMember_IsForbidden()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(adminClient, ValidPayload(name: "Member Cannot Edit"));

        try
        {
            var memberClient = await AuthenticatedClientAsync(AcmeMember);

            var response = await memberClient.PutAsJsonAsync(
                $"/resources/{created.Id}", ValidPayload(name: "Renamed By Member"));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // And nothing changed.
            var fetched = await adminClient.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{created.Id}");
            Assert.Equal("Member Cannot Edit", fetched!.Name);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    [Fact]
    public async Task Update_UnknownId_Returns404ResourceNotFound()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PutAsJsonAsync($"/resources/{Guid.NewGuid()}", ValidPayload());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    // AC-4. A TenantAdmin is still confined to their own tenant, and the answer
    // must be indistinguishable from the unknown-id case above — a write that
    // reported anything else would confirm the id exists elsewhere.
    [Fact]
    public async Task Update_AnotherTenantsRealId_Returns404AndChangesNothing()
    {
        var globexClient = await AuthenticatedClientAsync(GlobexAdmin);
        var globexResource = (await globexClient.GetFromJsonAsync<
            Application.Common.Pagination.PagedResult<
                Application.Features.Resources.ListResources.ResourceSummaryResponse>>("/resources"))!
            .Items.First();

        var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await acmeClient.PutAsJsonAsync(
            $"/resources/{globexResource.Id}", ValidPayload(name: "Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");

        // Globex's own resource is untouched.
        var unchanged = await globexClient.GetFromJsonAsync<ResourceDetailResponse>(
            $"/resources/{globexResource.Id}");
        Assert.Equal(globexResource.Name, unchanged!.Name);
    }

    // FR-3.5: an archived resource stays readable but takes no further edits.
    [Fact]
    public async Task Update_AnArchivedResource_Returns422ResourceArchived()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Archived Before Edit"));
        await ArchiveResourceDirectlyAsync(created.Id);

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{created.Id}", ValidPayload(name: "Edited While Archived"));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");

            // Still readable, still under its original name.
            var fetched = await client.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{created.Id}");
            Assert.Equal("Archived Before Edit", fetched!.Name);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // The seeded 3D Printer already carries an approver, so re-sending its own
    // representation proves ApproversRequired passes when one exists. Every
    // value sent back is the current one, so the seeded row is left as it was.
    [Fact]
    public async Task Update_RequiresApprovalOnAResourceThatHasAnApprover_Succeeds()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var printer = (await client.GetFromJsonAsync<
            Application.Common.Pagination.PagedResult<
                Application.Features.Resources.ListResources.ResourceSummaryResponse>>("/resources"))!
            .Items.Single(r => r.Name == "3D Printer");
        var before = await client.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{printer.Id}");

        var response = await client.PutAsJsonAsync(
            $"/resources/{printer.Id}",
            ValidPayload(
                name: before!.Name,
                description: before.Description,
                resourceType: before.ResourceType,
                capacity: before.Capacity,
                timeZoneId: before.TimeZoneId,
                requiresApproval: true,
                min: before.MinDurationMinutes,
                max: before.MaxDurationMinutes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<UpdateResourceResponse>();
        Assert.True(updated!.Resource.RequiresApproval);
        Assert.Equal(before.Name, updated.Resource.Name);
        Assert.Equal(before.Capacity, updated.Resource.Capacity);
    }

    // ---- PUT: the timezone-change notice (wp3-plan's "smaller calls") ----

    [Fact]
    public async Task Update_ChangingTheTimeZone_ReportsTheReinterpretedWindowCount()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Zone Change Room"));

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{created.Id}",
                ValidPayload(name: "Zone Change Room", timeZoneId: "Europe/Zagreb"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var updated = await response.Content.ReadFromJsonAsync<UpdateResourceResponse>();
            Assert.Equal("Europe/Zagreb", updated!.Resource.TimeZoneId);
            Assert.NotNull(updated.TimeZoneChange);
            Assert.Equal("America/New_York", updated.TimeZoneChange!.PreviousTimeZoneId);
            Assert.Equal("Europe/Zagreb", updated.TimeZoneChange.NewTimeZoneId);
            // A resource created through the API has no availability windows
            // yet — those arrive in Phase 3.
            Assert.Equal(0, updated.TimeZoneChange.ReinterpretedAvailabilityWindowCount);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // ---- PUT: capacity vs existing bookings (WP-3 decision D4) ----

    // The first real user of D4's carve-out: the rule cannot be tested without
    // Bookings rows, and dbo.CreateBooking is WP-4 work. The rows go in by raw
    // SQL — never LINQ or SaveChanges — so this can never be mistaken for a
    // production write path (CLAUDE.md §4.1).
    [Fact]
    public async Task Update_CapacityBelowUnitsAlreadyCommitted_Returns422()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Booked Room", capacity: 8));

        try
        {
            // Two overlapping future bookings: 3 + 2 = 5 units held at once.
            await InsertBookingAsync(created.Id, hoursFromNow: 24, durationHours: 2, quantity: 3, status: "Confirmed");
            await InsertBookingAsync(created.Id, hoursFromNow: 25, durationHours: 2, quantity: 2, status: "Pending");

            var response = await client.PutAsJsonAsync(
                $"/resources/{created.Id}", ValidPayload(name: "Booked Room", capacity: 4));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "CapacityBelowExistingBookings");

            var unchanged = await client.GetFromJsonAsync<ResourceDetailResponse>($"/resources/{created.Id}");
            Assert.Equal(8, unchanged!.Capacity);
        }
        finally
        {
            await DeleteBookingsForResourceAsync(created.Id);
            await DeleteResourceAsync(created.Id);
        }
    }

    // Decision 0005 counts *concurrent* units, not a total: the same two
    // bookings placed back to back never overlap, so a decrease to 3 is fine.
    [Fact]
    public async Task Update_CapacityAboveThePeakOfConcurrentUnits_Succeeds()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Sequential Room", capacity: 8));

        try
        {
            await InsertBookingAsync(created.Id, hoursFromNow: 24, durationHours: 2, quantity: 3, status: "Confirmed");
            await InsertBookingAsync(created.Id, hoursFromNow: 26, durationHours: 2, quantity: 2, status: "Confirmed");

            var response = await client.PutAsJsonAsync(
                $"/resources/{created.Id}", ValidPayload(name: "Sequential Room", capacity: 3));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var updated = await response.Content.ReadFromJsonAsync<UpdateResourceResponse>();
            Assert.Equal(3, updated!.Resource.Capacity);
        }
        finally
        {
            await DeleteBookingsForResourceAsync(created.Id);
            await DeleteResourceAsync(created.Id);
        }
    }

    // Cancelled bookings hold no units, and a past booking cannot be invalidated
    // by a change made now — FR-3.5's premise is that history stays as it was.
    [Fact]
    public async Task Update_CapacityIgnoresCancelledAndPastBookings()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "History Room", capacity: 8));

        try
        {
            await InsertBookingAsync(created.Id, hoursFromNow: 24, durationHours: 2, quantity: 8, status: "Cancelled");
            await InsertBookingAsync(created.Id, hoursFromNow: -48, durationHours: 2, quantity: 8, status: "Completed");
            await InsertBookingAsync(created.Id, hoursFromNow: -24, durationHours: 2, quantity: 8, status: "Confirmed");

            var response = await client.PutAsJsonAsync(
                $"/resources/{created.Id}", ValidPayload(name: "History Room", capacity: 1));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await DeleteBookingsForResourceAsync(created.Id);
            await DeleteResourceAsync(created.Id);
        }
    }

    // ---- Helpers ----

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

    private static async Task<ResourceDetailResponse> PostAndReadAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/resources", payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ResourceDetailResponse>())!;
    }

    private static async Task AssertReasonCodeAsync(HttpResponseMessage response, string expected)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, body.GetProperty("reasonCode").GetString());
    }

    private async Task<Guid> GetOrgIdAsync(string slug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.Organizations.Where(o => o.Slug == slug).Select(o => o.Id).SingleAsync();
    }

    // Reads the stored OrgId past both isolation layers, which is the only way
    // to assert the value the request never supplied. Same justification as
    // TenantIsolationTests' setup helpers.
    private async Task<Guid> ReadOrgIdOfResourceAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Resources
            .IgnoreQueryFilters()
            .Where(r => r.Id == resourceId)
            .Select(r => r.OrgId)
            .SingleAsync();
    }

    // Archived directly rather than through an endpoint: the archive endpoint is
    // step 4, and this test is about PUT refusing an already-archived resource.
    private async Task ArchiveResourceDirectlyAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId);
        resource.Archive(resource.CreatedByUserId, DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    // Fixture teardown, which the application itself never does (CLAUDE.md §4.5
    // deactivates and archives instead). A row left behind would break the
    // count assertions in the read and isolation tests.
    private async Task DeleteResourceAsync(Guid resourceId)
    {
        if (resourceId == Guid.Empty)
        {
            return;
        }

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        await context.Resources
            .IgnoreQueryFilters()
            .Where(r => r.Id == resourceId)
            .ExecuteDeleteAsync();
    }

    // WP-3 decision D4: raw SQL, never LINQ or SaveChanges. A booking write path
    // does not exist yet (dbo.CreateBooking is WP-4, CLAUDE.md §4.1), and the
    // capacity rule cannot be tested without rows. Raw SQL specifically, so this
    // cannot be mistaken for a production path and adds no domain method anyone
    // could reuse by accident.
    //
    // The connection needs an explicit RLS bypass, which is worth spelling out
    // because getting it wrong is silent. The policy's predicate is filter-only,
    // so it does not block the INSERT itself — but this statement *reads*
    // dbo.Resources and dbo.Users to derive OrgId and UserId, and a raw
    // connection with no session context sees zero rows in both. The first
    // version of this helper inserted nothing at all and reported no error.
    private async Task InsertBookingAsync(
        Guid resourceId,
        int hoursFromNow,
        int durationHours,
        int quantity,
        string status)
    {
        var startsAtUtc = DateTime.UtcNow.AddHours(hoursFromNow);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.Bookings
                (Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                 CreatedAtUtc, CreatedByUserId, UpdatedAtUtc)
            SELECT @Id, r.OrgId, r.Id, u.Id, @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @Status,
                   SYSUTCDATETIME(), u.Id, SYSUTCDATETIME()
            FROM dbo.Resources r
            CROSS JOIN (SELECT TOP 1 Id FROM dbo.Users WHERE Email = @Email) u
            WHERE r.Id = @ResourceId;
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid());
        command.Parameters.AddWithValue("@ResourceId", resourceId);
        command.Parameters.AddWithValue("@Email", AcmeMember);
        command.Parameters.AddWithValue("@StartsAtUtc", startsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", startsAtUtc.AddHours(durationHours));
        command.Parameters.AddWithValue("@Quantity", quantity);
        command.Parameters.AddWithValue("@Title", $"D4 fixture booking ({status})");
        command.Parameters.AddWithValue("@Status", status);

        var inserted = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, inserted);
    }

    // Bookings first, then the resource: FK_Bookings_Resources_SameOrg is
    // NoAction, so the resource cannot go while a booking references it.
    //
    // Bypass again, and for a subtler reason than the insert: an RLS filter
    // predicate applies to DELETE as well as SELECT, so without it this would
    // delete zero rows and report success — leaving bookings behind that then
    // block the resource delete.
    private async Task DeleteBookingsForResourceAsync(Guid resourceId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.Bookings WHERE ResourceId = @ResourceId;";
        command.Parameters.AddWithValue("@ResourceId", resourceId);
        await command.ExecuteNonQueryAsync();
    }

    // The same signal TenantSessionContextInterceptor sends for
    // TenantBypassScope (CLAUDE.md §4.2, decision 0013): TenantInit says a
    // context was set deliberately, TenantBypass says it is scopeless on
    // purpose. Session context is per-connection, so this has to be set on each
    // one the fixture opens.
    private static async Task EnterRlsBypassAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'TenantInit',   @value = 1;
            EXEC sp_set_session_context @key = N'TenantBypass', @value = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
