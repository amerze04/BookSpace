using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.ArchiveResource;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// WP-3 Phase 2 step 4: the archive endpoint, then the cross-cutting acceptance
// sweep for the resource-CRUD half of WP-3.
//
// The per-endpoint behavior is covered next door in ResourceReadEndpointTests and
// ResourceWriteEndpointTests. What this file asserts is the three things the WP's
// acceptance criteria state across *all* of them at once, which no single
// endpoint's tests can show:
//
//   1. An admin can publish a resource and it is then visible to a member.
//   2. Non-admins cannot create or edit resources — on every write route.
//   3. Another tenant's real id is a 404 on every route that takes one (AC-4).
//   4. Every reason code this phase can throw arrives with the status its
//      ErrorKind promises (the "clear, structured errors" criterion).
//
// Same state hygiene as the other two files: anything created is removed again,
// because the host and its database are shared across the collection.
[Collection(nameof(AuthenticationTestCollection))]
public class ResourceAcceptanceTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";

    public ResourceAcceptanceTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    private static object ValidPayload(string name = "Acceptance Room", int capacity = 4) =>
        new
        {
            name,
            description = "Created by the acceptance sweep",
            resourceType = "Room",
            capacity,
            timeZoneId = "America/New_York",
            requiresApproval = false,
            minDurationMinutes = 30,
            maxDurationMinutes = 240,
        };

    // ---- The archive endpoint (FR-3.5) ----

    [Fact]
    public async Task Archive_AsTenantAdmin_FlipsTheFlagAndKeepsTheResourceReadable()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Room To Archive"));

        try
        {
            var response = await client.PostAsync($"/resources/{created.Id}/archive", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var archived = await response.Content.ReadFromJsonAsync<ArchiveResourceCommandResponse>(TestJson.Options);
            Assert.True(archived!.IsArchived);
            // FR-3.5: archiving preserves the resource, it does not blank it.
            Assert.Equal("Room To Archive", archived.Name);
            Assert.Equal(created.Capacity, archived.Capacity);
            Assert.Equal(created.CreatedAtUtc, archived.CreatedAtUtc);

            // Still readable by id, and now hidden from the default list.
            var fetched = await client.GetFromJsonAsync<GetResourceQueryResponse>($"/resources/{created.Id}", TestJson.Options);
            Assert.True(fetched!.IsArchived);

            var defaultList = await client.GetFromJsonAsync<PagedResult<ListResourcesQueryResponse>>(
                $"/resources?pageSize={PagingDefaults.MaxPageSize}", TestJson.Options);
            Assert.DoesNotContain(created.Id, defaultList!.Items.Select(r => r.Id));

            var fullList = await client.GetFromJsonAsync<PagedResult<ListResourcesQueryResponse>>(
                $"/resources?pageSize={PagingDefaults.MaxPageSize}&includeArchived=true", TestJson.Options);
            Assert.Contains(created.Id, fullList!.Items.Select(r => r.Id));
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // A transition to a terminal state that already holds is not an error, and a
    // retry after a dropped response has to be safe.
    [Fact]
    public async Task Archive_IsIdempotent()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Room Archived Twice"));

        try
        {
            var first = await client.PostAsync($"/resources/{created.Id}/archive", content: null);
            var second = await client.PostAsync($"/resources/{created.Id}/archive", content: null);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var firstBody = await first.Content.ReadFromJsonAsync<ArchiveResourceCommandResponse>(TestJson.Options);
            var secondBody = await second.Content.ReadFromJsonAsync<ArchiveResourceCommandResponse>(TestJson.Options);

            // Byte-for-byte identical, including UpdatedAtUtc: the second call
            // changed nothing, so it must not move "last changed".
            Assert.Equal(firstBody, secondBody);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // Editing an archived resource is still refused — the idempotent archive is
    // the one place ResourceArchived is deliberately not thrown.
    [Fact]
    public async Task Archive_ThenEdit_IsStillRefused()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(client, ValidPayload(name: "Room Archived Then Edited"));

        try
        {
            await client.PostAsync($"/resources/{created.Id}/archive", content: null);

            var response = await client.PutAsJsonAsync($"/resources/{created.Id}", ValidPayload(name: "Renamed"));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    [Fact]
    public async Task Archive_UnknownId_Returns404ResourceNotFound()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsync($"/resources/{Guid.NewGuid()}/archive", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    // ---- AC: "an admin can publish a resource", end to end ----

    // The WP's first acceptance criterion, minus the availability and blackout
    // rules that Phases 3 and 4 add. What this shows is the handover: an admin
    // creates, and a member — a different principal, on the weaker policy — can
    // find it in the list and read its detail.
    [Fact]
    public async Task PublishedResource_IsImmediatelyVisibleToAMemberOfTheSameTenant()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(adminClient, ValidPayload(name: "Published Room", capacity: 12));

        try
        {
            var memberClient = await AuthenticatedClientAsync(AcmeMember);

            var list = await memberClient.GetFromJsonAsync<PagedResult<ListResourcesQueryResponse>>(
                $"/resources?pageSize={PagingDefaults.MaxPageSize}", TestJson.Options);
            var summary = list!.Items.Single(r => r.Id == created.Id);
            Assert.Equal("Published Room", summary.Name);
            Assert.Equal(12, summary.Capacity);

            var detail = await memberClient.GetFromJsonAsync<GetResourceQueryResponse>($"/resources/{created.Id}", TestJson.Options);
            ResourceResponseAssertions.AssertSameResource(created, detail!);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // ---- AC: "non-admins cannot create or edit resources", every write route ----

    // Parameterized over the routes rather than one test each, so a write
    // endpoint added later without an [Authorize] is a visible omission from this
    // list rather than an untested hole.
    public static TheoryData<string, string> NonAdminWriteAttempts() => new()
    {
        { "POST", "/resources" },
        { "PUT", "/resources/{id}" },
        { "POST", "/resources/{id}/archive" },
        { "PUT", "/resources/{id}/availability-windows" },
        { "PUT", "/resources/{id}/approvers" },
    };

    [Theory]
    [MemberData(nameof(NonAdminWriteAttempts))]
    public async Task EveryWriteRoute_IsForbiddenToAMember(string method, string routeTemplate)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(adminClient, ValidPayload(name: $"Guarded {method} {routeTemplate}"));

        try
        {
            var memberClient = await AuthenticatedClientAsync(AcmeMember);

            var response = await SendWriteAsync(memberClient, method, routeTemplate, created.Id);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // And the resource is untouched.
            var unchanged = await adminClient.GetFromJsonAsync<GetResourceQueryResponse>($"/resources/{created.Id}", TestJson.Options);
            ResourceResponseAssertions.AssertSameResource(created, unchanged!);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // The Approver role is not an admin either. Worth its own case because
    // AuthorizationPolicies.Approver sits between Member and TenantAdmin, and
    // "non-admin" has to mean every non-admin, not just Member.
    [Theory]
    [MemberData(nameof(NonAdminWriteAttempts))]
    public async Task EveryWriteRoute_IsForbiddenToAnApprover(string method, string routeTemplate)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var created = await PostAndReadAsync(adminClient, ValidPayload(name: $"Approver-guarded {method} {routeTemplate}"));

        try
        {
            var approverClient = await AuthenticatedClientAsync(AcmeApprover);

            var response = await SendWriteAsync(approverClient, method, routeTemplate, created.Id);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await DeleteResourceAsync(created.Id);
        }
    }

    // ---- AC-4: another tenant's real id, every route that takes one ----

    // The identifiers are real and the tenants hold identically-named resources,
    // which is AC-4's exact scenario. Every route must answer 404 with the same
    // reason code an id that exists nowhere gets — anything else confirms the id.
    [Theory]
    [InlineData("GET", "/resources/{id}")]
    [InlineData("PUT", "/resources/{id}")]
    [InlineData("POST", "/resources/{id}/archive")]
    [InlineData("PUT", "/resources/{id}/availability-windows")]
    [InlineData("PUT", "/resources/{id}/approvers")]
    [InlineData("GET", "/resources/{id}/availability?from=2026-09-07&to=2026-09-07")]
    public async Task EveryRouteTakingAnId_TreatsAnotherTenantsRealIdAsNotFound(
        string method,
        string routeTemplate)
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);

        var response = method == "GET"
            ? await acmeClient.GetAsync(routeTemplate.Replace("{id}", globexResourceId.ToString()))
            : await SendWriteAsync(acmeClient, method, routeTemplate, globexResourceId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");

        // Nothing happened on the other side of the boundary.
        var globexClient = await AuthenticatedClientAsync(GlobexAdmin);
        var untouched = await globexClient.GetFromJsonAsync<GetResourceQueryResponse>(
            $"/resources/{globexResourceId}", TestJson.Options);
        Assert.False(untouched!.IsArchived);
    }

    // A cross-tenant 404 has to be indistinguishable from an ordinary one, not
    // merely the same status — a differing title or extra field would leak just
    // as effectively.
    [Fact]
    public async Task CrossTenantNotFound_IsIndistinguishableFromAnUnknownId()
    {
        var globexResourceId = await GetAnyResourceIdAsync("globex");
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var crossTenant = await ReadProblemWithoutPerRequestFieldsAsync(
            await client.GetAsync($"/resources/{globexResourceId}"));
        var unknown = await ReadProblemWithoutPerRequestFieldsAsync(
            await client.GetAsync($"/resources/{Guid.NewGuid()}"));

        Assert.Equal(unknown, crossTenant);
    }

    // ---- AC: "the API returns clear, structured errors" ----

    // Every reason code this phase can throw, with the status its ErrorKind
    // promises (docs/decisions/0016). One table, so a code that starts arriving
    // as the wrong status fails here rather than in whichever endpoint test
    // happens to cover it.
    //
    // Not listed, because no thrower exists yet: BlackoutPeriod (Phase 4) and the
    // six booking codes (WP-4). ReasonCodesTests already proves those exist and
    // are unique; this proves the ones with throwers behave.
    [Theory]
    [InlineData("ResourceNotFound", HttpStatusCode.NotFound)]
    [InlineData("InvalidTimeZone", HttpStatusCode.BadRequest)]
    [InlineData("ResourceArchived", HttpStatusCode.UnprocessableEntity)]
    [InlineData("OverlappingAvailabilityWindow", HttpStatusCode.Conflict)]
    [InlineData("ApproverNotEligible", HttpStatusCode.UnprocessableEntity)]
    [InlineData("ValidationFailed", HttpStatusCode.BadRequest)]
    public async Task EveryReasonCodeThisPhaseThrows_ArrivesWithTheStatusItsKindPromises(
        string reasonCode,
        HttpStatusCode expectedStatus)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var (response, cleanupResourceId) = await ProvokeAsync(client, reasonCode);

        try
        {
            Assert.Equal(expectedStatus, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(reasonCode, body.GetProperty("reasonCode").GetString());
            // Every error response is a ProblemDetails carrying the correlation
            // id, so a client's bug report maps onto a log line.
            Assert.Equal((int)expectedStatus, body.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
        }
        finally
        {
            await DeleteResourceAsync(cleanupResourceId);
        }
    }

    // CapacityBelowExistingBookings needs Bookings rows, which is decision D4's
    // raw-SQL fixture territory — covered in ResourceWriteEndpointTests rather
    // than duplicated here. This asserts the remaining half of the same claim:
    // the exception message never reaches the client (docs/decisions/0016 — the
    // message is for the log, the reason code is the contract).
    [Fact]
    public async Task AnErrorResponse_NeverCarriesTheExceptionMessage()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var unknownId = Guid.NewGuid();

        var response = await client.GetAsync($"/resources/{unknownId}");
        var raw = await response.Content.ReadAsStringAsync();

        // The handler's message is "Resource {id} was not found in the current
        // tenant." — the id would be in the body if the message leaked.
        Assert.DoesNotContain(unknownId.ToString(), raw);
        Assert.DoesNotContain("current tenant", raw);
    }

    // ---- Helpers ----

    private async Task<HttpResponseMessage> SendWriteAsync(
        HttpClient client,
        string method,
        string routeTemplate,
        Guid resourceId)
    {
        var route = routeTemplate.Replace("{id}", resourceId.ToString());

        return method switch
        {
            "POST" when route.EndsWith("/archive", StringComparison.Ordinal) =>
                await client.PostAsync(route, content: null),
            "POST" => await client.PostAsJsonAsync(route, ValidPayload(name: "Should Not Be Created")),
            // The schedule endpoint takes its own payload shape; sending a
            // resource body would be a 400 from the validator and would prove
            // nothing about the policy.
            "PUT" when route.EndsWith("/availability-windows", StringComparison.Ordinal) =>
                await client.PutAsJsonAsync(route, new { windows = new[] { new { weekday = "Monday", opensAt = "09:00:00", closesAt = "17:00:00" } } }),
            // Likewise its own shape. An empty list is a valid payload and is
            // enough to prove the policy refuses the call before any handler runs.
            "PUT" when route.EndsWith("/approvers", StringComparison.Ordinal) =>
                await client.PutAsJsonAsync(route, new { approverUserIds = Array.Empty<Guid>() }),
            "PUT" => await client.PutAsJsonAsync(route, ValidPayload(name: "Should Not Be Renamed")),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unhandled method."),
        };
    }

    // Provokes one specific reason code through a real request. Returns any
    // resource that needs cleaning up afterwards.
    private async Task<(HttpResponseMessage Response, Guid CleanupResourceId)> ProvokeAsync(
        HttpClient client,
        string reasonCode)
    {
        switch (reasonCode)
        {
            case "ResourceNotFound":
                return (await client.GetAsync($"/resources/{Guid.NewGuid()}"), Guid.Empty);

            case "InvalidTimeZone":
                return (
                    await client.PostAsJsonAsync("/resources", new
                    {
                        name = "Bad Zone",
                        resourceType = "Room",
                        capacity = 1,
                        timeZoneId = "Eastern Standard Time",
                        requiresApproval = false,
                    }),
                    Guid.Empty);

            case "ResourceArchived":
            {
                var created = await PostAndReadAsync(client, ValidPayload(name: "Archived For The Sweep"));
                await client.PostAsync($"/resources/{created.Id}/archive", content: null);
                return (
                    await client.PutAsJsonAsync($"/resources/{created.Id}", ValidPayload(name: "Renamed")),
                    created.Id);
            }

            case "OverlappingAvailabilityWindow":
            {
                var created = await PostAndReadAsync(client, ValidPayload(name: "Overlapping For The Sweep"));
                return (
                    await client.PutAsJsonAsync(
                        $"/resources/{created.Id}/availability-windows",
                        new
                        {
                            windows = new[]
                            {
                                new { weekday = "Monday", opensAt = "09:00:00", closesAt = "13:00:00" },
                                new { weekday = "Monday", opensAt = "12:00:00", closesAt = "17:00:00" },
                            },
                        }),
                    created.Id);
            }

            case "ApproverNotEligible":
            {
                var created = await PostAndReadAsync(client, ValidPayload(name: "Ineligible For The Sweep"));
                return (
                    await client.PutAsJsonAsync(
                        $"/resources/{created.Id}/approvers",
                        new { approverUserIds = new[] { Guid.NewGuid() } }),
                    created.Id);
            }

            case "ValidationFailed":
                return (await client.GetAsync("/resources?pageSize=0"), Guid.Empty);

            default:
                throw new ArgumentOutOfRangeException(nameof(reasonCode), reasonCode, "Unhandled reason code.");
        }
    }

    // Strips the two fields that legitimately differ per request, so what is left
    // is the part two 404s must share exactly.
    private static async Task<string> ReadProblemWithoutPerRequestFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        body!.Remove("traceId");
        body.Remove("correlationId");

        return string.Join(
            "|",
            body.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.ToString()}"));
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

    private static async Task<CreateResourceCommandResponse> PostAndReadAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/resources", payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;
    }

    private static async Task AssertReasonCodeAsync(HttpResponseMessage response, string expected)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, body.GetProperty("reasonCode").GetString());
    }

    // Both isolation layers bypassed, same justification as TenantIsolationTests:
    // setup has to see across tenants to know what real id to probe with.
    private async Task<Guid> GetAnyResourceIdAsync(string orgSlug)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var orgId = await context.Organizations.Where(o => o.Slug == orgSlug).Select(o => o.Id).SingleAsync();

        using var _ = TenantBypassScope.Enter();
        var resource = await context.Resources.IgnoreQueryFilters().FirstAsync(r => r.OrgId == orgId);
        return resource.Id;
    }

    // Fixture teardown, which the application never does (CLAUDE.md §4.5
    // archives instead) — a row left behind would break the count assertions in
    // the read and isolation tests.
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
