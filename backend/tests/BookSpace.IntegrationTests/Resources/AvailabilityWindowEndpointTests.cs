using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// WP-3 Phase 3, FR-3.2: PUT /resources/{id}/availability-windows through the
// real pipeline.
//
// The rules themselves are unit-tested against fakes. What these prove is the
// parts a fake cannot: that the TenantAdmin policy is wired, that the rows
// actually reach dbo.AvailabilityWindows past the tenant guard and RLS, that
// replacing a set really DELETEs the old rows rather than accumulating them,
// and that the schedule then appears on the read detail.
//
// Same state hygiene as the sibling files: every test creates its own resource
// and removes it again, because the host and its database are shared across the
// collection and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class AvailabilityWindowEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";

    public AvailabilityWindowEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static object ValidResource(string name) =>
        new
        {
            name,
            description = "Created by the availability window tests",
            resourceType = "Room",
            capacity = 4,
            timeZoneId = "America/New_York",
            requiresApproval = false,
            minDurationMinutes = 30,
            maxDurationMinutes = 240,
        };

    // Weekday travels as its name, not its ordinal — the API serializes enums as
    // strings (Program.cs). Writing the payload by hand here rather than reusing
    // the command record is deliberate: this is what a client actually sends, so
    // a rename that broke the wire would show up as a failure rather than
    // silently following along.
    private static object Window(string weekday, string opensAt, string closesAt) =>
        new { weekday, opensAt, closesAt };

    // ---- The happy path ----

    [Fact]
    public async Task Replace_AsTenantAdmin_StoresTheScheduleAndReturnsItOrdered()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Happy Path");

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new
                {
                    windows = new[]
                    {
                        Window("Wednesday", "09:00:00", "17:00:00"),
                        Window("Monday", "13:00:00", "17:00:00"),
                        Window("Monday", "09:00:00", "12:00:00"),
                    },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ReplaceAvailabilityWindowsCommandResponse>(
                TestJson.Options);

            Assert.Equal(resource.Id, body!.ResourceId);
            Assert.Equal(3, body.AvailabilityWindows.Count);

            // Ordered by weekday then opening time, regardless of the order sent.
            Assert.Equal(
                new[]
                {
                    (DayOfWeek.Monday, new TimeOnly(9, 0)),
                    (DayOfWeek.Monday, new TimeOnly(13, 0)),
                    (DayOfWeek.Wednesday, new TimeOnly(9, 0)),
                },
                body.AvailabilityWindows.Select(w => (w.Weekday, w.OpensAt)));

            Assert.All(body.AvailabilityWindows, w => Assert.NotEqual(Guid.Empty, w.Id));

            // And the rows are really in the database, scoped to the caller's
            // own tenant — decision 0014 put AvailabilityWindows inside all three
            // §4.2 mechanisms, so a wrong OrgId would not have saved at all.
            var stored = await StoredWindowCountAsync(resource.Id);
            Assert.Equal(3, stored);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // The read detail is where a member sees the schedule (FR-3.2). It is on
    // GET /resources/{id} and deliberately not on the PUT /resources/{id}
    // payload, so a resource edit cannot wipe a schedule by omission.
    [Fact]
    public async Task Replace_ThenRead_ShowsTheScheduleOnTheResourceDetail()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, "Windows On The Detail");

        try
        {
            // A newly created resource has no schedule at all.
            var before = await adminClient.GetFromJsonAsync<GetResourceQueryResponse>(
                $"/resources/{resource.Id}", TestJson.Options);
            Assert.Empty(before!.AvailabilityWindows);

            await adminClient.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Friday", "08:30:00", "16:45:00") } });

            // Read back as a *member*: a non-admin has to see availability in
            // order to book against it.
            var memberClient = await AuthenticatedClientAsync(AcmeMember);
            var detail = await memberClient.GetFromJsonAsync<GetResourceQueryResponse>(
                $"/resources/{resource.Id}", TestJson.Options);

            var window = Assert.Single(detail!.AvailabilityWindows);
            Assert.Equal(DayOfWeek.Friday, window.Weekday);
            Assert.Equal(new TimeOnly(8, 30), window.OpensAt);
            Assert.Equal(new TimeOnly(16, 45), window.ClosesAt);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // Replace, not merge. The point of the endpoint's shape: the old rows have to
    // actually go, or a second PUT would double the schedule.
    [Fact]
    public async Task Replace_DiscardsThePreviousSchedule()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Replaced");

        try
        {
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new
                {
                    windows = new[]
                    {
                        Window("Monday", "09:00:00", "17:00:00"),
                        Window("Tuesday", "09:00:00", "17:00:00"),
                    },
                });

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Saturday", "10:00:00", "14:00:00") } });

            var body = await response.Content.ReadFromJsonAsync<ReplaceAvailabilityWindowsCommandResponse>(
                TestJson.Options);

            var window = Assert.Single(body!.AvailabilityWindows);
            Assert.Equal(DayOfWeek.Saturday, window.Weekday);

            // One row in the table, not three — the cascade on
            // FK_AvailabilityWindows_Resources_SameOrg really deleted the old set.
            Assert.Equal(1, await StoredWindowCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // Sending the same schedule twice leaves the same schedule. Replace-the-set
    // buys this for free; per-row POST/DELETE endpoints could not.
    [Fact]
    public async Task Replace_IsIdempotent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Sent Twice");
        var payload = new { windows = new[] { Window("Thursday", "09:00:00", "17:00:00") } };

        try
        {
            var first = await client.PutAsJsonAsync($"/resources/{resource.Id}/availability-windows", payload);
            var second = await client.PutAsJsonAsync($"/resources/{resource.Id}/availability-windows", payload);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var firstBody = await first.Content.ReadFromJsonAsync<ReplaceAvailabilityWindowsCommandResponse>(
                TestJson.Options);
            var secondBody = await second.Content.ReadFromJsonAsync<ReplaceAvailabilityWindowsCommandResponse>(
                TestJson.Options);

            // The schedule is the same; the ids are not, because the set is
            // rebuilt rather than diffed. Worth asserting explicitly so the churn
            // is a documented property rather than a surprise to a client that
            // tried to cache a window id.
            Assert.Equal(
                firstBody!.AvailabilityWindows.Select(w => (w.Weekday, w.OpensAt, w.ClosesAt)),
                secondBody!.AvailabilityWindows.Select(w => (w.Weekday, w.OpensAt, w.ClosesAt)));
            Assert.NotEqual(
                firstBody.AvailabilityWindows.Single().Id,
                secondBody.AvailabilityWindows.Single().Id);

            Assert.Equal(1, await StoredWindowCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // An empty array is a legitimate schedule, not a malformed request: it means
    // "this resource opens at no time at all".
    [Fact]
    public async Task Replace_WithAnEmptyArray_ClearsTheSchedule()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Cleared");

        try
        {
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Monday", "09:00:00", "17:00:00") } });

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = Array.Empty<object>() });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ReplaceAvailabilityWindowsCommandResponse>(
                TestJson.Options);
            Assert.Empty(body!.AvailabilityWindows);
            Assert.Equal(0, await StoredWindowCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // ---- Rejections ----

    // 409, not 422: each window is individually valid and it is the set that
    // contradicts itself (ErrorKind.Conflict, docs/decisions/0016).
    [Fact]
    public async Task Replace_OverlappingWindows_Returns409AndChangesNothing()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Overlapping");

        try
        {
            await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Monday", "09:00:00", "17:00:00") } });

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new
                {
                    windows = new[]
                    {
                        Window("Tuesday", "09:00:00", "13:00:00"),
                        Window("Tuesday", "12:00:00", "17:00:00"),
                    },
                });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertReasonCodeAsync(response, "OverlappingAvailabilityWindow");

            // The previous schedule survived: the rule check runs before any
            // mutator, so a rejected request writes nothing.
            var detail = await client.GetFromJsonAsync<GetResourceQueryResponse>(
                $"/resources/{resource.Id}", TestJson.Options);
            var window = Assert.Single(detail!.AvailabilityWindows);
            Assert.Equal(DayOfWeek.Monday, window.Weekday);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // Adjacent is not overlapping — ClosesAt is exclusive. Asserted over HTTP as
    // well as in the unit tests, because this is the boundary an admin is most
    // likely to hit by accident when splitting a day into morning and afternoon.
    [Fact]
    public async Task Replace_AdjacentWindows_IsAccepted()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Adjacent");

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new
                {
                    windows = new[]
                    {
                        Window("Monday", "09:00:00", "12:00:00"),
                        Window("Monday", "12:00:00", "17:00:00"),
                    },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // CK_AvailabilityWindows_Window, surfaced as a 400 naming the window rather
    // than a 500 from the domain ArgumentException underneath it.
    [Fact]
    public async Task Replace_WindowThatClosesBeforeItOpens_Returns400WithTheFieldNamed()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Backwards");

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Monday", "17:00:00", "09:00:00") } });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            // Read the body once: HttpContent here is a single-use stream, so
            // AssertReasonCodeAsync followed by a second read throws
            // ObjectDisposedException rather than failing the assertion.
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());

            var errors = body.GetProperty("errors");
            Assert.True(errors.TryGetProperty("Windows[0].ClosesAt", out _));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    // FR-3.5: an archived resource refuses this exactly as it refuses an edit.
    [Fact]
    public async Task Replace_OnAnArchivedResource_Returns422ResourceArchived()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows On Archived");

        try
        {
            await client.PostAsync($"/resources/{resource.Id}/archive", content: null);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Monday", "09:00:00", "17:00:00") } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Replace_UnknownResource_Returns404ResourceNotFound()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PutAsJsonAsync(
            $"/resources/{Guid.NewGuid()}/availability-windows",
            new { windows = new[] { Window("Monday", "09:00:00", "17:00:00") } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    // AC-4. The route hangs off a resource id, so it inherits the parent's
    // cross-tenant answer — and must, or an admin could write a schedule onto
    // another tenant's room.
    [Fact]
    public async Task Replace_AnotherTenantsRealResource_Returns404AndWritesNothing()
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);
        var before = await StoredWindowCountAsync(globexResourceId);

        var response = await acmeClient.PutAsJsonAsync(
            $"/resources/{globexResourceId}/availability-windows",
            new { windows = new[] { Window("Monday", "09:00:00", "17:00:00") } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");

        // Nothing happened on the other side of the boundary — including no
        // deletion, which a replace-the-set endpoint would do first if the
        // lookup had succeeded.
        Assert.Equal(before, await StoredWindowCountAsync(globexResourceId));

        var globexClient = await AuthenticatedClientAsync(GlobexAdmin);
        var untouched = await globexClient.GetFromJsonAsync<GetResourceQueryResponse>(
            $"/resources/{globexResourceId}", TestJson.Options);
        Assert.Equal(before, untouched!.AvailabilityWindows.Count);
    }

    // WP-3's AC: "non-admins cannot create or edit resources". A schedule is part
    // of the resource, so a member is refused by the policy before any handler
    // runs — a plain 403 with no reason code.
    [Fact]
    public async Task Replace_AsAMember_IsForbidden()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, "Windows Guarded");

        try
        {
            var memberClient = await AuthenticatedClientAsync(AcmeMember);

            var response = await memberClient.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Monday", "09:00:00", "17:00:00") } });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await StoredWindowCountAsync(resource.Id));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }


    // Sub-second times are refused, not rounded (the columns are time(0)).
    // Asserted over HTTP as well as in the validator tests for one specific
    // reason: it has to be the *validator* that refuses it, with a reason code
    // and a named field, rather than System.Text.Json rejecting the payload at
    // the model binder — those produce different bodies, and only one of them is
    // the documented contract.
    [Fact]
    public async Task Replace_SubSecondTimes_Returns400FromTheValidator()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Windows Sub Second");

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/availability-windows",
                new { windows = new[] { Window("Monday", "09:00:00.5", "17:00:00.25") } });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());

            var errors = body.GetProperty("errors");
            Assert.True(errors.TryGetProperty("Windows[0].OpensAt", out _));
            Assert.True(errors.TryGetProperty("Windows[0].ClosesAt", out _));
        }
        finally
        {
            await DeleteResourceAsync(resource.Id);
        }
    }
    // ---- Helpers ----

    private async Task<CreateResourceCommandResponse> CreateResourceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/resources", ValidResource(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;
    }

    // Counted through EF with both isolation layers bypassed, so the assertion is
    // about what is physically in the table rather than about what the API is
    // willing to show — which is the whole point when the claim is "nothing was
    // written to another tenant's resource".
    private async Task<int> StoredWindowCountAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Set<Domain.Entities.AvailabilityWindow>()
            .IgnoreQueryFilters()
            .CountAsync(w => w.ResourceId == resourceId);
    }

    private async Task<Guid> GetAnyResourceIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources.IgnoreQueryFilters().FirstAsync(r => r.OrgId == orgId);
        return resource.Id;
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

    private static async Task AssertReasonCodeAsync(HttpResponseMessage response, string expected)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, body.GetProperty("reasonCode").GetString());
    }

    // Fixture teardown, which the application never does (CLAUDE.md §4.5
    // archives instead) — a row left behind would break the count assertions in
    // the read and isolation tests. The windows go with the resource via the
    // cascade, so only the resource has to be deleted here.
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
}
