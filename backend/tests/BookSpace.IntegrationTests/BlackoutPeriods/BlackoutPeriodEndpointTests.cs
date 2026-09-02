using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;
using BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.BlackoutPeriods;

// WP-3 Phase 4, FR-3.4 and decision 0001: the full lifecycle of
// /resources/{id}/blackout-periods through the real pipeline.
//
// The rules are unit-tested against fakes. What these prove is the parts a fake
// cannot: that the policies are wired, that rows reach dbo.BlackoutPeriods past
// the tenant guard and RLS (decision 0014), and — the reason this phase needed
// integration tests at all — that decision 0001's cascade really updates
// dbo.Bookings and inserts dbo.Notifications in the same transaction as the
// blackout.
//
// Bookings are inserted by raw SQL, per decision 0017: dbo.CreateBooking is WP-4
// work (CLAUDE.md §4.1), so there is no booking write path to build the cascade's
// input through yet. Raw SQL specifically, so it cannot be mistaken for a
// production path.
//
// Same state hygiene as the Resources files: every test creates its own resource
// and removes it again, because the host and its database are shared across the
// collection and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class BlackoutPeriodEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public BlackoutPeriodEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // Whole seconds, and a UTC Kind: the validator rejects fractional seconds
    // (datetime2(0) would round them) and an instant with no zone.
    private static DateTime HoursFromNow(int hours) =>
        new DateTime(
            DateTime.UtcNow.AddHours(hours).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            DateTimeKind.Utc);

    // Written by hand rather than by reusing the command record: this is what a
    // client actually sends, so a rename that broke the wire shows up as a
    // failure instead of silently following along. The "o" format carries the Z.
    private static object BlackoutPayload(DateTime startsAtUtc, DateTime endsAtUtc, string? reason = "Boiler service") =>
        new
        {
            startsAtUtc = startsAtUtc.ToString("o"),
            endsAtUtc = endsAtUtc.ToString("o"),
            reason,
        };

    private static object ValidResource(string name) =>
        new
        {
            name,
            description = "Created by the blackout period tests",
            resourceType = "Room",
            capacity = 4,
            timeZoneId = "America/New_York",
            requiresApproval = false,
            minDurationMinutes = 30,
            maxDurationMinutes = 240,
        };

    // ---- The happy path ----

    [Fact]
    public async Task Create_AsTenantAdmin_StoresTheBlackoutAndReturns201()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Happy Path");

        try
        {
            var starts = HoursFromNow(24);
            var ends = HoursFromNow(30);

            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(starts, ends));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.NotEqual(Guid.Empty, body!.Id);
            Assert.Equal(resource.Id, body.ResourceId);
            Assert.Equal(starts, body.StartsAtUtc);
            Assert.Equal(ends, body.EndsAtUtc);
            Assert.Equal("Boiler service", body.Reason);

            // Empty, not absent: the cascade ran and found nothing.
            Assert.Empty(body.CancelledBookings);

            // And the row is really in the table, scoped to the caller's own
            // tenant — decision 0014 put BlackoutPeriods inside all three §4.2
            // mechanisms, so a wrong OrgId would not have saved at all.
            Assert.Equal(1, await StoredBlackoutCountAsync(resource.Id));

            // Every DateTime read back from the database carries Kind=Utc
            // (CLAUDE.md §4.3's value converter), so the JSON keeps its Z and a
            // browser client does not parse it as local time.
            Assert.Equal(DateTimeKind.Utc, body.StartsAtUtc.Kind);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The response's Location header points at the list, which is where the new
    // row is visible — there is no per-blackout GET.
    [Fact]
    public async Task Create_ReturnsALocationHeaderPointingAtTheList()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Location Header");

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            Assert.Contains(
                $"/resources/{resource.Id}/blackout-periods",
                response.Headers.Location!.ToString());
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Overlapping blackouts are allowed (owner's call, 2026-09-02), deliberately
    // unlike overlapping availability windows: the union of two blackouts is
    // still blacked out, so there is nothing to disambiguate.
    [Fact]
    public async Task Create_AllowsTwoBlackoutsThatOverlapEachOther()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Overlaps Allowed");

        try
        {
            var first = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(36), "Boiler service"));
            var second = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(30), HoursFromNow(48), "Deep clean"));

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.Equal(2, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Decision 0001: the cascade ----

    [Fact]
    public async Task Create_CancelsOverlappingBookingsAndEnqueuesOneNotificationEach()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Cascade");

        try
        {
            // Two inside the blackout, one outside it.
            var confirmed = await InsertBookingAsync(resource.Id, hoursFromNow: 25, durationHours: 1, status: "Confirmed");
            var pending = await InsertBookingAsync(resource.Id, hoursFromNow: 27, durationHours: 1, status: "Pending");
            var untouched = await InsertBookingAsync(resource.Id, hoursFromNow: 50, durationHours: 1, status: "Confirmed");

            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            // Reported in the reply, not silently applied (PRD AC-2, FR-5.x).
            Assert.Equal(
                new[] { confirmed, pending }.Order(),
                body!.CancelledBookings.Select(b => b.BookingId).Order());

            // And really cancelled in the database, regardless of which status
            // they held: decision 0001 gives a blackout absolute priority.
            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(confirmed));
            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(pending));
            Assert.Equal(BookingStatus.Confirmed, await StoredBookingStatusAsync(untouched));

            // One Notifications row per cancellation, for the not-yet-built
            // dispatch job to pick up (CLAUDE.md §7). Its unique constraint is
            // what makes the eventual send idempotent (AC-6).
            Assert.Equal(1, await StoredNotificationCountAsync(confirmed));
            Assert.Equal(1, await StoredNotificationCountAsync(pending));
            Assert.Equal(0, await StoredNotificationCountAsync(untouched));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Adjacency is not overlap: the interval is half-open, so a booking ending
    // exactly when the blackout starts survives. The room is usable up to that
    // instant.
    [Fact]
    public async Task Create_LeavesABookingThatOnlyTouchesTheBlackoutsEdge()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Adjacency");

        try
        {
            // Exact instants, both derived from one base: the booking ends at the
            // *same* instant the blackout starts. Deriving them independently
            // from two UtcNow calls was enough to make this intermittently a
            // one-second overlap — see InsertBookingAsync.
            var starts = HoursFromNow(24);
            var before = await InsertBookingAsync(
                resource.Id, starts.AddHours(-1), starts, status: "Confirmed");

            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(starts, starts.AddHours(6)));

            var body = await response.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.Empty(body!.CancelledBookings);
            Assert.Equal(BookingStatus.Confirmed, await StoredBookingStatusAsync(before));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The guard that protects history. Nothing in this system writes
    // BookingStatus.Completed — there is no Complete() and no job in §7 that sets
    // it — so a meeting that happened and was checked into is still Confirmed.
    // A blackout straddling now must not reach back and cancel it.
    [Fact]
    public async Task Create_DoesNotCancelABookingThatHasAlreadyFinished()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Spares History");

        try
        {
            var past = await InsertBookingAsync(resource.Id, hoursFromNow: -6, durationHours: 1, status: "Confirmed");
            var future = await InsertBookingAsync(resource.Id, hoursFromNow: 2, durationHours: 1, status: "Confirmed");

            // Starts in the past and runs into the future — the "the room flooded
            // this morning" case, which is legal.
            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(-12), HoursFromNow(12)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.Equal(future, Assert.Single(body!.CancelledBookings).BookingId);
            Assert.Equal(BookingStatus.Confirmed, await StoredBookingStatusAsync(past));
            Assert.Equal(0, await StoredNotificationCountAsync(past));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A terminal status holds no claim on the resource, so there is nothing to
    // cancel and nobody to notify.
    [Fact]
    public async Task Create_IgnoresBookingsInATerminalStatus()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Skips Terminal");

        try
        {
            var cancelled = await InsertBookingAsync(resource.Id, hoursFromNow: 25, durationHours: 1, status: "Cancelled");
            var rejected = await InsertBookingAsync(resource.Id, hoursFromNow: 26, durationHours: 1, status: "Rejected");

            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            var body = await response.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.Empty(body!.CancelledBookings);
            Assert.Equal(0, await StoredNotificationCountAsync(cancelled));
            Assert.Equal(0, await StoredNotificationCountAsync(rejected));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Decision 0001 records the acting admin on BlackoutPeriods.CreatedByUserId
    // and deliberately not on the booking: a rule cancelled it, not a person
    // acting on that booking.
    [Fact]
    public async Task Create_LeavesNoCancellingUserOnTheCancelledBooking()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Cancel Audit");

        try
        {
            var booking = await InsertBookingAsync(resource.Id, hoursFromNow: 25, durationHours: 1, status: "Confirmed");

            await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30), "Boiler service"));

            var (cancelledBy, reason) = await StoredCancellationAsync(booking);

            Assert.Null(cancelledBy);

            // A text snapshot, not a foreign key, so it still says why after the
            // blackout row is hard-deleted.
            Assert.Contains("Boiler service", reason);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- The read side ----

    [Fact]
    public async Task List_ReturnsThePagedScheduleInChronologicalOrder()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout List Order");

        try
        {
            // Created out of order on purpose.
            await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(72), HoursFromNow(78), "Third"));
            await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30), "First"));
            await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(48), HoursFromNow(54), "Second"));

            var response = await client.GetAsync($"/resources/{resource.Id}/blackout-periods");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content
                .ReadFromJsonAsync<PagedResult<ListBlackoutPeriodsQueryResponse>>(TestJson.Options);

            Assert.Equal(3, body!.TotalCount);
            Assert.Equal(new[] { "First", "Second", "Third" }, body.Items.Select(b => b.Reason));
            Assert.All(body.Items, b => Assert.Equal(resource.Id, b.ResourceId));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A member picking a time needs to see when the room is blocked — the same
    // reason the read detail carries the availability windows.
    [Fact]
    public async Task List_IsVisibleToAPlainMember()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, "Blackout Member Read");

        try
        {
            await adminClient.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            var memberClient = await AuthenticatedClientAsync(AcmeMember);
            var response = await memberClient.GetAsync($"/resources/{resource.Id}/blackout-periods");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content
                .ReadFromJsonAsync<PagedResult<ListBlackoutPeriodsQueryResponse>>(TestJson.Options);

            Assert.Equal(1, body!.TotalCount);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Overlap, not containment: a blackout that started before the window and
    // runs into it is exactly what "what blocks next week" has to return.
    [Fact]
    public async Task List_FiltersByOverlapWithTheRequestedRange()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout List Filter");

        try
        {
            await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(20), HoursFromNow(28), "Straddles the window start"));
            await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(100), HoursFromNow(106), "Well outside"));

            var from = HoursFromNow(24).ToString("o");
            var to = HoursFromNow(48).ToString("o");
            var response = await client.GetAsync(
                $"/resources/{resource.Id}/blackout-periods?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

            var body = await response.Content
                .ReadFromJsonAsync<PagedResult<ListBlackoutPeriodsQueryResponse>>(TestJson.Options);

            Assert.Equal(1, body!.TotalCount);
            Assert.Equal("Straddles the window start", body.Items.Single().Reason);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task List_ReturnsAnEmptyPageForAResourceWithNoBlackouts()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout List Empty");

        try
        {
            var response = await client.GetAsync($"/resources/{resource.Id}/blackout-periods");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content
                .ReadFromJsonAsync<PagedResult<ListBlackoutPeriodsQueryResponse>>(TestJson.Options);

            Assert.Equal(0, body!.TotalCount);
            Assert.Empty(body.Items);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The distinction the handler's resource check exists for: an empty page and
    // a missing resource are different answers, and a client has to be able to
    // tell "nothing blocked" from "no such room".
    [Fact]
    public async Task List_Returns404ForAResourceThatDoesNotExist()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/resources/{Guid.NewGuid()}/blackout-periods");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    // ---- Rejections ----

    // AC-4: a real Globex resource id is indistinguishable from one that exists
    // nowhere, and nothing in the response hints that it exists.
    [Fact]
    public async Task Create_Returns404ForAnotherTenantsRealResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var globexResourceId = await GetAnyResourceIdAsync("globex");

        // Compared before and after rather than against zero: SeedData already
        // gives each org's open resource a public-holiday blackout, so the
        // absolute count is not the claim being made here — "this request added
        // nothing" is.
        var before = await StoredBlackoutCountAsync(globexResourceId);

        var response = await client.PostAsJsonAsync(
            $"/resources/{globexResourceId}/blackout-periods",
            BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");

        // Nothing was written to Globex's resource.
        Assert.Equal(before, await StoredBlackoutCountAsync(globexResourceId));
    }

    // Byte-identical to the cross-tenant answer above, which is the point.
    [Fact]
    public async Task Create_Returns404ForAResourceThatExistsNowhere()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.PostAsJsonAsync(
            $"/resources/{Guid.NewGuid()}/blackout-periods",
            BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    [Fact]
    public async Task Create_Returns422ForAnArchivedResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout On Archived");

        try
        {
            await client.PostAsync($"/resources/{resource.Id}/archive", content: null);

            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");
            Assert.Equal(0, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Owner's call, 2026-09-02. The new reason code this phase introduces.
    [Fact]
    public async Task Create_Returns422ForABlackoutEntirelyInThePast()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Elapsed");

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(-30), HoursFromNow(-24)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "BlackoutPeriodElapsed");
            Assert.Equal(0, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Create_Returns400ForAnInvertedInterval()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Inverted");

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(30), HoursFromNow(24)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertReasonCodeAsync(response, "ValidationFailed");
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // An instant with no zone is refused rather than guessed at (CLAUDE.md §4.3).
    // Sent as a raw string, because a serialized DateTime always carries one.
    [Fact]
    public async Task Create_Returns400ForAnInstantWithNoZone()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout No Zone");

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                new
                {
                    startsAtUtc = "2027-01-04T08:00:00",
                    endsAtUtc = "2027-01-04T18:00:00",
                    reason = "No zone",
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertReasonCodeAsync(response, "ValidationFailed");
            Assert.Equal(0, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- RBAC ----

    // WP-3's AC "non-admins cannot create or edit resources", applied to the
    // rules hanging off one. Approver sits between Member and TenantAdmin, so
    // "non-admin" has to mean every non-admin; SysAdmin is refused too, because
    // TenantMember requires the orgId claim decision 0012 omits for them.
    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    [InlineData(SysAdmin)]
    public async Task Create_IsForbiddenToEveryNonAdmin(string email)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, $"Blackout RBAC {email}");

        try
        {
            var client = await AuthenticatedClientAsync(email);

            var response = await client.PostAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Create_IsUnauthorizedWithNoToken()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/resources/{Guid.NewGuid()}/blackout-periods",
            BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_IsUnauthorizedWithNoToken()
    {
        var client = _host.CreateClient();

        var response = await client.GetAsync($"/resources/{Guid.NewGuid()}/blackout-periods");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Edit (step 2) ----

    [Fact]
    public async Task Update_AsTenantAdmin_RevisesTheBlackoutAndReturnsIt()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update Happy Path");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            var newStarts = HoursFromNow(26);
            var newEnds = HoursFromNow(40);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(newStarts, newEnds, "Deep clean"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<UpdateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(newStarts, body.StartsAtUtc);
            Assert.Equal(newEnds, body.EndsAtUtc);
            Assert.Equal("Deep clean", body.Reason);

            // Still one row — an edit, not a second blackout.
            Assert.Equal(1, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A full representation, so an omitted reason clears it rather than leaving
    // it as it was (docs/decisions/0015).
    [Fact]
    public async Task Update_ClearsTheReasonWhenTheRequestOmitsIt()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update Clears Reason");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30), reason: null));

            var body = await response.Content.ReadFromJsonAsync<UpdateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.Null(body!.Reason);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The case decision 0001 names explicitly: created, or edited to a wider
    // range. The widened blackout reaches a booking the original never touched.
    [Fact]
    public async Task Update_CancelsBookingsTheWidenedIntervalNowCovers()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update Cascade");

        try
        {
            var inside = await InsertBookingAsync(resource.Id, hoursFromNow: 25, durationHours: 1, status: "Confirmed");
            var outsideAtFirst = await InsertBookingAsync(resource.Id, hoursFromNow: 34, durationHours: 1, status: "Confirmed");

            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            // The first blackout took `inside` only.
            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(inside));
            Assert.Equal(BookingStatus.Confirmed, await StoredBookingStatusAsync(outsideAtFirst));

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(40)));

            var body = await response.Content.ReadFromJsonAsync<UpdateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            // Only the newly covered booking is reported: the one the original
            // interval already cancelled was reported when it happened, and
            // repeating it would read as a fresh cancellation.
            Assert.Equal(outsideAtFirst, Assert.Single(body!.CancelledBookings).BookingId);
            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(outsideAtFirst));

            // One notification each, from the two separate cascades. The unique
            // constraint would have refused a duplicate for the first booking.
            Assert.Equal(1, await StoredNotificationCountAsync(inside));
            Assert.Equal(1, await StoredNotificationCountAsync(outsideAtFirst));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Forwards only. Narrowing restores nothing, because a cancellation is
    // irreversible — the owner has already been told their meeting is off.
    [Fact]
    public async Task Update_RestoresNothingWhenTheIntervalIsNarrowed()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update Narrows");

        try
        {
            var booking = await InsertBookingAsync(resource.Id, hoursFromNow: 25, durationHours: 1, status: "Confirmed");
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(booking));

            // Narrowed so the booking now falls outside it.
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(28), HoursFromNow(30)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<UpdateBlackoutPeriodCommandResponse>(
                TestJson.Options);

            Assert.Empty(body!.CancelledBookings);
            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(booking));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The second 404, and the reason it is its own code: the resource is fine,
    // the blackout is not.
    [Fact]
    public async Task Update_Returns404WithBlackoutPeriodNotFoundForAnUnknownBlackout()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update Unknown Id");

        try
        {
            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{Guid.NewGuid()}",
                BlackoutPayload(HoursFromNow(24), HoursFromNow(30)));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertReasonCodeAsync(response, "BlackoutPeriodNotFound");
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A real blackout reached through the wrong resource is a 404, not an edit
    // applied to the wrong room.
    [Fact]
    public async Task Update_Returns404ForABlackoutBelongingToAnotherResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var owner = await CreateResourceAsync(client, "Blackout Update Owner");
        var other = await CreateResourceAsync(client, "Blackout Update Other");

        try
        {
            var created = await CreateBlackoutAsync(client, owner.Id, HoursFromNow(24), HoursFromNow(30));

            var response = await client.PutAsJsonAsync(
                $"/resources/{other.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(48), HoursFromNow(54)));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertReasonCodeAsync(response, "BlackoutPeriodNotFound");

            // And the real one is untouched.
            var unchanged = await client.GetFromJsonAsync<PagedResult<ListBlackoutPeriodsQueryResponse>>(
                $"/resources/{owner.Id}/blackout-periods", TestJson.Options);
            Assert.Equal(created.StartsAtUtc, unchanged!.Items.Single().StartsAtUtc);
        }
        finally
        {
            await CleanUpAsync(owner.Id);
            await CleanUpAsync(other.Id);
        }
    }

    // AC-4: another tenant's real blackout id gives the same answer as one that
    // exists nowhere.
    [Fact]
    public async Task Update_Returns404ForAnotherTenantsRealBlackout()
    {
        var globexClient = await AuthenticatedClientAsync(GlobexAdmin);
        var globexResource = await CreateResourceAsync(globexClient, "Globex Blackout Target");

        try
        {
            var globexBlackout = await CreateBlackoutAsync(
                globexClient, globexResource.Id, HoursFromNow(24), HoursFromNow(30));

            var acmeClient = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await acmeClient.PutAsJsonAsync(
                $"/resources/{globexResource.Id}/blackout-periods/{globexBlackout.Id}",
                BlackoutPayload(HoursFromNow(48), HoursFromNow(54)));

            // ResourceNotFound rather than BlackoutPeriodNotFound, because the
            // resource is checked first and is already invisible to Acme.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceNotFound");
        }
        finally
        {
            await CleanUpAsync(globexResource.Id);
        }
    }

    [Fact]
    public async Task Update_Returns422WhenMovedEntirelyIntoThePast()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update Elapsed");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(-30), HoursFromNow(-24)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "BlackoutPeriodElapsed");
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Update_Returns422ForAnArchivedResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Update On Archived");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));
            await client.PostAsync($"/resources/{resource.Id}/archive", content: null);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(48), HoursFromNow(54)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    [InlineData(SysAdmin)]
    public async Task Update_IsForbiddenToEveryNonAdmin(string email)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, $"Blackout Update RBAC {email}");

        try
        {
            var created = await CreateBlackoutAsync(adminClient, resource.Id, HoursFromNow(24), HoursFromNow(30));
            var client = await AuthenticatedClientAsync(email);

            var response = await client.PutAsJsonAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}",
                BlackoutPayload(HoursFromNow(48), HoursFromNow(54)));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Delete (step 2) ----

    [Fact]
    public async Task Delete_AsTenantAdmin_RemovesTheRowAndReturns204()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Delete Happy Path");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            var response = await client.DeleteAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // A real hard delete, and the first in this system: the row is gone,
            // not flagged. CLAUDE.md §4.5 is about users and resources.
            Assert.Equal(0, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Deleting a blackout means stop blocking future bookings, never undo. The
    // cancellation and its reason survive the row it pointed at, because the
    // reason is a text snapshot rather than a foreign key.
    [Fact]
    public async Task Delete_LeavesCancelledBookingsCancelledWithTheirReasonIntact()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Delete Keeps History");

        try
        {
            var booking = await InsertBookingAsync(resource.Id, hoursFromNow: 25, durationHours: 1, status: "Confirmed");
            var created = await CreateBlackoutAsync(
                client, resource.Id, HoursFromNow(24), HoursFromNow(30), "Boiler service");

            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(booking));

            await client.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{created.Id}");

            Assert.Equal(0, await StoredBlackoutCountAsync(resource.Id));
            Assert.Equal(BookingStatus.Cancelled, await StoredBookingStatusAsync(booking));

            var (_, reason) = await StoredCancellationAsync(booking);
            Assert.Contains("Boiler service", reason);

            // The notification stays queued too — the owner was told, and that
            // cannot be taken back either.
            Assert.Equal(1, await StoredNotificationCountAsync(booking));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Deliberately not idempotent: a second DELETE is a 404. This endpoint cannot
    // tell already-deleted from another-tenant (AC-4), so a blanket 204 would
    // silently accept the latter. Contrast POST /resources/{id}/archive, which
    // *is* idempotent because the row is still there to inspect.
    [Fact]
    public async Task Delete_Returns404OnASecondCall()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Delete Twice");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));

            var first = await client.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{created.Id}");
            var second = await client.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{created.Id}");

            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
            await AssertReasonCodeAsync(second, "BlackoutPeriodNotFound");
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Delete_Returns404ForABlackoutBelongingToAnotherResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var owner = await CreateResourceAsync(client, "Blackout Delete Owner");
        var other = await CreateResourceAsync(client, "Blackout Delete Other");

        try
        {
            var created = await CreateBlackoutAsync(client, owner.Id, HoursFromNow(24), HoursFromNow(30));

            var response = await client.DeleteAsync(
                $"/resources/{other.Id}/blackout-periods/{created.Id}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertReasonCodeAsync(response, "BlackoutPeriodNotFound");

            // And the real one is still there.
            Assert.Equal(1, await StoredBlackoutCountAsync(owner.Id));
        }
        finally
        {
            await CleanUpAsync(owner.Id);
            await CleanUpAsync(other.Id);
        }
    }

    // Consistency over convenience: an archived resource accepts no writes is a
    // rule an admin can hold in their head, and an archived resource's blackouts
    // block nothing anyway.
    [Fact]
    public async Task Delete_Returns422ForAnArchivedResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Blackout Delete On Archived");

        try
        {
            var created = await CreateBlackoutAsync(client, resource.Id, HoursFromNow(24), HoursFromNow(30));
            await client.PostAsync($"/resources/{resource.Id}/archive", content: null);

            var response = await client.DeleteAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}");

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertReasonCodeAsync(response, "ResourceArchived");
            Assert.Equal(1, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Theory]
    [InlineData(AcmeMember)]
    [InlineData(AcmeApprover)]
    [InlineData(SysAdmin)]
    public async Task Delete_IsForbiddenToEveryNonAdmin(string email)
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, $"Blackout Delete RBAC {email}");

        try
        {
            var created = await CreateBlackoutAsync(adminClient, resource.Id, HoursFromNow(24), HoursFromNow(30));
            var client = await AuthenticatedClientAsync(email);

            var response = await client.DeleteAsync(
                $"/resources/{resource.Id}/blackout-periods/{created.Id}");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, await StoredBlackoutCountAsync(resource.Id));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Helpers ----

    // Step 2's tests all need an existing blackout to act on, so creating one
    // goes through the real endpoint rather than a raw insert: it keeps the
    // fixture honest (the row is exactly what the API produces) and means a
    // regression in create shows up here too.
    private static async Task<CreateBlackoutPeriodCommandResponse> CreateBlackoutAsync(
        HttpClient client,
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        string? reason = "Boiler service")
    {
        var response = await client.PostAsJsonAsync(
            $"/resources/{resourceId}/blackout-periods",
            BlackoutPayload(startsAtUtc, endsAtUtc, reason));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreateBlackoutPeriodCommandResponse>(
            TestJson.Options))!;
    }

    private async Task<CreateResourceCommandResponse> CreateResourceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/resources", ValidResource(name));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;
    }

    // Counted with both isolation layers bypassed, so the assertion is about what
    // is physically in the table rather than about what the API is willing to
    // show — which is the whole point when the claim is "nothing was written to
    // another tenant's resource".
    private async Task<int> StoredBlackoutCountAsync(Guid resourceId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.BlackoutPeriods
            .IgnoreQueryFilters()
            .CountAsync(b => b.ResourceId == resourceId);
    }

    private async Task<BookingStatus> StoredBookingStatusAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => b.Status)
            .SingleAsync();
    }

    private async Task<(Guid? CancelledByUserId, string? CancellationReason)> StoredCancellationAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => ValueTuple.Create(b.CancelledByUserId, b.CancellationReason))
            .SingleAsync();
    }

    // Notifications is not tenant-scoped (it has no OrgId), so no bypass is
    // needed for the query filter — but it is reached through a scope for
    // consistency with its siblings.
    private async Task<int> StoredNotificationCountAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        return await context.Notifications
            .IgnoreQueryFilters()
            .CountAsync(n => n.BookingId == bookingId && n.Kind == NotificationKind.Cancelled);
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

    // Decision 0017's carve-out from CLAUDE.md §4.1: a fixture may insert
    // Bookings rows with raw SQL, never LINQ and never SaveChanges, and only in a
    // fixture. The production write path does not exist yet — dbo.CreateBooking
    // is WP-4 — and decision 0001's cascade cannot be tested without rows to
    // cascade over.
    //
    // The connection needs an explicit RLS bypass, which is worth spelling out
    // because getting it wrong is silent: this statement *reads* dbo.Resources
    // and dbo.Users to derive OrgId and UserId, and a raw connection with no
    // session context sees zero rows in both. The first version of the sibling
    // helper in ResourceWriteEndpointTests inserted nothing at all and reported
    // no error.
    //
    // Returns the id, unlike that sibling, because these tests assert on the
    // status of one specific booking after the cascade.
    // Offsets are resolved to instants *here*, once, and the end derives from the
    // start rather than from a second DateTime.UtcNow — so both are whole seconds
    // and exactly durationHours apart.
    //
    // That matters more than it looks: Bookings.StartsAtUtc/EndsAtUtc are
    // datetime2(0), which **rounds** on write (CLAUDE.md §4.3). An untruncated
    // UtcNow can therefore round *up* a second while a truncated blackout bound
    // rounds down, which is enough to turn an adjacency test into a one-second
    // overlap intermittently. Found exactly that way.
    private static Task<Guid> InsertBookingAsync(
        Guid resourceId,
        int hoursFromNow,
        int durationHours,
        string status)
    {
        var startsAtUtc = HoursFromNow(hoursFromNow);
        return InsertBookingAsync(resourceId, startsAtUtc, startsAtUtc.AddHours(durationHours), status);
    }

    private static async Task<Guid> InsertBookingAsync(
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        string status)
    {
        var bookingId = Guid.NewGuid();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.Bookings
                (Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                 CreatedAtUtc, CreatedByUserId, UpdatedAtUtc)
            SELECT @Id, r.OrgId, r.Id, u.Id, @StartsAtUtc, @EndsAtUtc, 1, @Title, @Status,
                   SYSUTCDATETIME(), u.Id, SYSUTCDATETIME()
            FROM dbo.Resources r
            CROSS JOIN (SELECT TOP 1 Id FROM dbo.Users WHERE Email = @Email) u
            WHERE r.Id = @ResourceId;
            """;
        command.Parameters.AddWithValue("@Id", bookingId);
        command.Parameters.AddWithValue("@ResourceId", resourceId);
        command.Parameters.AddWithValue("@Email", AcmeMember);
        command.Parameters.AddWithValue("@StartsAtUtc", startsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", endsAtUtc);
        command.Parameters.AddWithValue("@Title", $"Blackout cascade fixture ({status})");
        command.Parameters.AddWithValue("@Status", status);

        var inserted = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, inserted);

        return bookingId;
    }

    // Order matters. Notifications reference Bookings with ON DELETE CASCADE, so
    // they go with the bookings; Bookings reference Resources with NoAction
    // (CLAUDE.md §4.5), so the resource cannot go while a booking references it.
    // Blackouts cascade with the resource.
    //
    // Fixture teardown, which the application never does — a row left behind
    // would break the resource-count assertions in the sibling files.
    private async Task CleanUpAsync(Guid resourceId)
    {
        if (resourceId == Guid.Empty)
        {
            return;
        }

        await DeleteBookingsForResourceAsync(resourceId);

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        await context.Resources
            .IgnoreQueryFilters()
            .Where(r => r.Id == resourceId)
            .ExecuteDeleteAsync();
    }

    // Bypass again, and for a subtler reason than the insert: an RLS filter
    // predicate applies to DELETE as well as SELECT, so without it this would
    // delete zero rows and report success — leaving bookings behind that then
    // block the resource delete.
    private static async Task DeleteBookingsForResourceAsync(Guid resourceId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.Bookings WHERE ResourceId = @ResourceId;";
        command.Parameters.AddWithValue("@ResourceId", resourceId);
        await command.ExecuteNonQueryAsync();
    }

    // The same signal TenantSessionContextInterceptor sends for TenantBypassScope
    // (CLAUDE.md §4.2, decision 0013): TenantInit says a context was set
    // deliberately, TenantBypass says it is scopeless on purpose. Session context
    // is per-connection, so this has to be set on each one the fixture opens.
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
