using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Bookings.CancelBooking;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// WP-4 Phase 2b: POST /bookings/{id}/cancel through the real pipeline — real
// middleware, real JwtBearer handler, real authorization policies, real SQL
// Server.
//
// This file settles the rest of the work package's fourth Week-3 task (FR-4.4)
// and its third acceptance criterion — "a member can cancel their own booking;
// the slot is freed" — and the freeing is asserted three ways rather than
// assumed: the availability endpoint offers the interval again, a *new* booking
// for the same slot succeeds, and on an exclusive resource the same slot is
// refused before the cancel and accepted after it.
//
// It also carries decision 0002 end to end: a TenantAdmin cancels a booking they
// do not own, `CancelledByUserId` records them distinctly from the owner, and the
// owner gets a `Notifications` row — while a member cancelling their own gets
// none (owner's call, 2026-09-08).
//
// Same state hygiene as its siblings: every test creates its own resource and
// removes it again, since the host's database is shared across the collection.
// And the same fixture trap — `member2@acme.test` is unusable, see AcmeColleague.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingCancelEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string GlobexMember = "member1@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    // Not member2@acme.test, which AuthenticationEndpointTests
    // .Refresh_UserDeactivatedSinceLogin deactivates permanently — see the note
    // on BookingReadEndpointTests.AcmeColleague for the full story. An Approver
    // is a non-admin, so it serves as "another member" correctly.
    private const string AcmeColleague = AcmeApprover;

    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=BookSpace_AuthTests;Trusted_Connection=True;TrustServerCertificate=True;";

    public BookingCancelEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // A fixed future Thursday in whole seconds — datetime2(0) rounds on write
    // (CLAUDE.md §4.3).
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 3, 11, hour, minute, 0, DateTimeKind.Utc);

    // ---- The happy path (FR-4.4) -------------------------------------------

    [Fact]
    public async Task Cancel_LetsAMemberCancelTheirOwnBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var response = await client.PostAsJsonAsync(
                $"/bookings/{created.Id}/cancel", new { reason = "No longer needed" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CancelBookingCommandResponse>(
                TestJson.Options))!;

            Assert.Equal(created.Id, body.Id);
            Assert.Equal(BookingStatus.Cancelled, body.Status);
            Assert.Equal(created.UserId, body.CancelledByUserId);
            Assert.Equal(created.UserId, body.UserId);
            Assert.Equal("No longer needed", body.CancellationReason);

            // The freed interval — the point of the reply.
            Assert.Equal(resource, body.ResourceId);
            Assert.Equal(At(9), body.StartsAtUtc);
            Assert.Equal(At(10), body.EndsAtUtc);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Cancelled';",
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The row is not deleted (CLAUDE.md §4.5) — it keeps its interval and its
    // owner, and gains the three cancellation columns. Read back through
    // GET /bookings/{id} rather than SQL, so the transition is visible on the
    // API a client actually uses.
    [Fact]
    public async Task Cancel_PreservesTheBookingAndRecordsWhoAndWhen()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            (await client.PostAsJsonAsync(
                $"/bookings/{created.Id}/cancel", new { reason = "Plans changed" }))
                .EnsureSuccessStatusCode();

            var detail = (await (await client.GetAsync($"/bookings/{created.Id}"))
                .Content.ReadFromJsonAsync<GetBookingQueryResponse>(TestJson.Options))!;

            Assert.Equal(BookingStatus.Cancelled, detail.Status);
            Assert.Equal(created.UserId, detail.CancelledByUserId);
            Assert.NotNull(detail.CancelledAtUtc);
            Assert.Equal("Plans changed", detail.CancellationReason);

            // Untouched by the transition.
            Assert.Equal(At(9), detail.StartsAtUtc);
            Assert.Equal(At(10), detail.EndsAtUtc);
            Assert.Equal(created.UserId, detail.UserId);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // A reason is optional: the column is nullable and "the meeting is off" is
    // often all there is to say. The body may be omitted entirely, which is why
    // the controller's parameter is nullable.
    [Fact]
    public async Task Cancel_AcceptsNoReasonAndNoBodyAtAll()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var first = await CreateBookingAsync(client, resource, At(9), At(10));
            var second = await CreateBookingAsync(client, resource, At(11), At(12));

            var withEmptyBody = await client.PostAsJsonAsync(
                $"/bookings/{first.Id}/cancel", new { });
            Assert.Equal(HttpStatusCode.OK, withEmptyBody.StatusCode);

            var withNoBody = await client.PostAsync($"/bookings/{second.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, withNoBody.StatusCode);

            var body = (await withNoBody.Content.ReadFromJsonAsync<CancelBookingCommandResponse>(
                TestJson.Options))!;
            Assert.Null(body.CancellationReason);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Cancel_WorksOnAPendingBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            Assert.Equal(BookingStatus.Pending, created.Status);

            var response = await client.PostAsync($"/bookings/{created.Id}/cancel", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- AC: "the slot is freed" -------------------------------------------

    // On an exclusive resource, so the slot genuinely is unavailable before the
    // cancel — the strongest form of the assertion, because the second create
    // could only succeed if the first booking stopped holding its unit.
    [Fact]
    public async Task Cancel_FreesTheSlotForANewBooking()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var first = await CreateBookingAsync(client, resource, At(9), At(10));

            // Before: the slot is taken, so the same interval is refused.
            var blocked = await client.PostAsJsonAsync(
                "/bookings", Booking(resource, At(9), At(10)));
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("SlotUnavailable", await ReasonCodeAsync(blocked));

            (await client.PostAsync($"/bookings/{first.Id}/cancel", null)).EnsureSuccessStatusCode();

            // After: the same interval is accepted, by a different member even.
            var colleague = await AuthenticatedClientAsync(AcmeColleague);
            var second = await colleague.PostAsJsonAsync(
                "/bookings", Booking(resource, At(9), At(10)));

            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The other half of "freed": the availability endpoint stops counting the
    // cancelled booking's units. Only Pending and Confirmed consume capacity, so
    // a Cancelled row has to become invisible to the calculator — asserted
    // through the endpoint rather than the sweep, since that is what a client
    // sees.
    [Fact]
    public async Task Cancel_ReturnsTheCapacityToTheAvailabilityQuery()
    {
        var resource = await CreateBookableResourceAsync(capacity: 1);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            // 09:00-10:00 is taken out of a continuous day, leaving two spans.
            Assert.Equal(2, await BookableIntervalCountAsync(client, resource));

            (await client.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            // Back to one continuous span.
            var intervals = await BookableIntervalsAsync(client, resource);
            var whole = Assert.Single(intervals);
            Assert.Equal(At(0), whole.StartUtc);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // On a pooled resource the unit comes back rather than the time: capacity 2
    // fully booked shows no bookable interval over the slot, and one
    // cancellation restores it.
    [Fact]
    public async Task Cancel_ReturnsOneUnitOfAPool()
    {
        var resource = await CreateBookableResourceAsync(capacity: 2);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var colleague = await AuthenticatedClientAsync(AcmeColleague);

            var mine = await CreateBookingAsync(member, resource, At(9), At(10));
            await CreateBookingAsync(colleague, resource, At(9), At(10));

            // Both units held, so the slot is gone from the answer entirely.
            Assert.Equal(2, await BookableIntervalCountAsync(member, resource));

            (await member.PostAsync($"/bookings/{mine.Id}/cancel", null)).EnsureSuccessStatusCode();

            // One unit free again across the whole day.
            Assert.Single(await BookableIntervalsAsync(member, resource));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Decision 0002: a TenantAdmin cancels another member's booking -----

    [Fact]
    public async Task Cancel_LetsAnAdminCancelAnotherMembersBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await admin.PostAsJsonAsync(
                $"/bookings/{created.Id}/cancel", new { reason = "Room repurposed" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<CancelBookingCommandResponse>(
                TestJson.Options))!;

            // Decision 0002's whole point: the actor is recorded distinctly from
            // the owner, so the member can see it was not them.
            var adminId = await ScalarAsync<Guid>(
                "SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeAdmin);

            Assert.Equal(adminId, body.CancelledByUserId);
            Assert.Equal(created.UserId, body.UserId);
            Assert.NotEqual(body.UserId, body.CancelledByUserId);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // FR-8.1 / decision 0002: the affected user is told. Rows only — nothing
    // sends anything yet (CLAUDE.md §7), and UQ_Notifications_Once is what makes
    // the eventual send idempotent.
    [Fact]
    public async Task Cancel_ByAnAdminEnqueuesANotificationForTheOwner()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            // Addressed to the owner, not the admin who acted.
            Assert.Equal(1, await CountAsync(
                """
                SELECT COUNT(*) FROM dbo.Notifications
                WHERE BookingId = @p0 AND Kind = 'Cancelled' AND RecipientUserId = @p1
                  AND SentAtUtc IS NULL;
                """,
                created.Id,
                created.UserId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The suppression settled on 2026-09-08: decision 0002's requirement is that
    // the *affected user* is told, and emailing someone the news they just made
    // is noise — the 200 body has already told them.
    [Fact]
    public async Task Cancel_ByTheOwnerEnqueuesNoNotification()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            (await client.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Notifications WHERE BookingId = @p0 AND Kind = 'Cancelled';",
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The suppression keys off actor-versus-owner, not off the role — so an
    // admin cancelling their *own* booking also gets no notification. This is
    // the case a role-based check would have got wrong.
    [Fact]
    public async Task Cancel_ByAnAdminOfTheirOwnBookingEnqueuesNoNotification()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var created = await CreateBookingAsync(admin, resource, At(9), At(10));

            (await admin.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Notifications WHERE BookingId = @p0 AND Kind = 'Cancelled';",
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Who may not cancel ------------------------------------------------

    // A 404, never a 403: a 403 would confirm the booking exists and leak who is
    // holding which resource (BookingNotFoundException). And nothing is
    // cancelled — asserted, because a handler that loaded first and refused
    // second could have mutated before throwing.
    [Fact]
    public async Task Cancel_RefusesAnotherMembersBookingWithA404AndChangesNothing()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var owner = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(owner, resource, At(9), At(10));

            var colleague = await AuthenticatedClientAsync(AcmeColleague);
            var response = await colleague.PostAsync($"/bookings/{created.Id}/cancel", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Confirmed';",
                created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // AC-4, on the write path this time: another member's booking, another
    // tenant's booking and a nonexistent id are byte-identical. Compared
    // body-for-body rather than status-for-status, because three matching status
    // codes with three different bodies would still leak.
    [Fact]
    public async Task Cancel_IsByteIdenticalForEveryUnreachableBooking()
    {
        var acmeResource = await CreateBookableResourceAsync();
        var globexResource = await CreateBookableResourceAsync(admin: GlobexAdmin);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);

            var colleaguesBooking = await CreateBookingAsync(
                await AuthenticatedClientAsync(AcmeColleague), acmeResource, At(9), At(10));
            var globexBooking = await CreateBookingAsync(
                await AuthenticatedClientAsync(GlobexMember), globexResource, At(9), At(10));

            var otherMember = await member.PostAsync($"/bookings/{colleaguesBooking.Id}/cancel", null);
            var otherTenant = await member.PostAsync($"/bookings/{globexBooking.Id}/cancel", null);
            var nonexistent = await member.PostAsync($"/bookings/{Guid.NewGuid()}/cancel", null);

            Assert.Equal(HttpStatusCode.NotFound, otherMember.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);

            var expected = await ComparableBodyAsync(nonexistent);
            Assert.Equal(expected, await ComparableBodyAsync(otherMember));
            Assert.Equal(expected, await ComparableBodyAsync(otherTenant));
        }
        finally
        {
            await CleanUpAsync(acmeResource);
            await CleanUpAsync(globexResource, GlobexAdmin);
        }
    }

    // Decision 0002 is scoped to "their own tenant", and the tenant filter is
    // what enforces it — an admin's dropped owner filter still cannot reach
    // across an org boundary (CLAUDE.md §4.2, AC-4).
    [Fact]
    public async Task Cancel_RefusesAnAdminReachingIntoAnotherTenant()
    {
        var globexResource = await CreateBookableResourceAsync(admin: GlobexAdmin);

        try
        {
            var globexBooking = await CreateBookingAsync(
                await AuthenticatedClientAsync(GlobexMember), globexResource, At(9), At(10));

            var acmeAdmin = await AuthenticatedClientAsync(AcmeAdmin);
            var response = await acmeAdmin.PostAsync($"/bookings/{globexBooking.Id}/cancel", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Confirmed';",
                globexBooking.Id));
        }
        finally
        {
            await CleanUpAsync(globexResource, GlobexAdmin);
        }
    }

    // ---- BookingNotCancellable ---------------------------------------------

    // Deliberately not idempotent, unlike archive: a second cancellation would
    // overwrite CancelledByUserId, CancelledAtUtc and the reason with a second
    // actor's, so the record of who called the meeting off would quietly change.
    [Fact]
    public async Task Cancel_RefusesASecondCancellation()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            (await client.PostAsJsonAsync($"/bookings/{created.Id}/cancel", new { reason = "First" }))
                .EnsureSuccessStatusCode();

            var second = await client.PostAsJsonAsync(
                $"/bookings/{created.Id}/cancel", new { reason = "Second" });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
            Assert.Equal("BookingNotCancellable", await ReasonCodeAsync(second));

            // The first actor's record survives untouched.
            var detail = (await (await client.GetAsync($"/bookings/{created.Id}"))
                .Content.ReadFromJsonAsync<GetBookingQueryResponse>(TestJson.Options))!;
            Assert.Equal("First", detail.CancellationReason);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The elapsed half of BookingNotCancellable, and the half nothing enforced
    // before Phase 2b. The booking has to be inserted by decision 0017's
    // raw-SQL fixture, because POST /bookings refuses a wholly-past interval
    // (BookingInThePast) — so the endpoint cannot produce this state itself.
    [Fact]
    public async Task Cancel_RefusesABookingThatHasAlreadyEnded()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var memberId = await ScalarAsync<Guid>(
                "SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeMember);
            var bookingId = await InsertPastBookingAsync(resource, memberId);

            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsync($"/bookings/{bookingId}/cancel", null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("BookingNotCancellable", await ReasonCodeAsync(response));

            // Still Confirmed — history was not rewritten.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0 AND Status = 'Confirmed';",
                bookingId));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // In progress is not ended: the room is free from now on, which is the whole
    // point of cancelling. The test is on EndsAtUtc, deliberately not
    // StartsAtUtc (decision 0019's rule, reapplied).
    [Fact]
    public async Task Cancel_AllowsABookingAlreadyUnderWay()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var memberId = await ScalarAsync<Guid>(
                "SELECT Id FROM dbo.Users WHERE Email = @p0;", AcmeMember);
            var bookingId = await InsertStraddlingBookingAsync(resource, memberId);

            var client = await AuthenticatedClientAsync(AcmeMember);
            var response = await client.PostAsync($"/bookings/{bookingId}/cancel", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- Shape and authorization -------------------------------------------

    [Fact]
    public async Task Cancel_RefusesAnOverLongReason()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var response = await client.PostAsJsonAsync(
                $"/bookings/{created.Id}/cancel", new { reason = new string('x', 301) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Cancel_RequiresAuthentication()
    {
        var client = _host.CreateClient();

        var response = await client.PostAsync($"/bookings/{Guid.NewGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // TenantMember requires the orgId claim, which decision 0012 omits for a
    // SysAdmin — PRD §2 keeps the Platform Operator out of tenant booking
    // content. A 403 rather than a 404, because it is RBAC refusing the route
    // rather than a rule hiding a row.
    [Fact]
    public async Task Cancel_RefusesASysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        var response = await client.PostAsync($"/bookings/{Guid.NewGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Every error body is a ProblemDetails carrying the correlation id, and the
    // exception's message never reaches the client (decision 0016).
    [Fact]
    public async Task Cancel_ReturnsProblemDetailsWithACorrelationId()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.PostAsync($"/bookings/{Guid.NewGuid()}/cancel", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("BookingNotFound", body.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not found for the current caller", raw, StringComparison.Ordinal);
    }

    // ---- The list and detail agree with the cancel -------------------------

    // A cancelled booking stays visible on both reads — §4.5 deletes nothing, so
    // "my bookings" still includes it, and `?status=Cancelled` is how a client
    // asks for exactly these.
    [Fact]
    public async Task Cancel_LeavesTheBookingVisibleOnTheReads()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            (await client.PostAsync($"/bookings/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

            var listed = await (await client.GetAsync(
                $"/bookings?resourceId={resource}&status=Cancelled"))
                .Content.ReadFromJsonAsync<JsonElement>();

            var items = listed.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Equal(created.Id, items[0].GetProperty("id").GetGuid());
            Assert.Equal("Cancelled", items[0].GetProperty("status").GetString());
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

    // Decision 0017's carve-out, and the narrowing WP-4 records for it: new tests
    // use the real path, but a state the endpoint *cannot* produce still needs
    // raw SQL. A wholly-past booking is exactly that — POST /bookings refuses it
    // with BookingInThePast, so there is no other way to arrange the
    // already-ended case. Raw SQL specifically so it cannot be mistaken for a
    // production path.
    private async Task<Guid> InsertPastBookingAsync(Guid resourceId, Guid userId) =>
        await InsertBookingAsync(resourceId, userId, startOffsetHours: -2, endOffsetHours: -1);

    // Started an hour ago, ends in an hour — the "in progress" case, which the
    // endpoint also cannot create (its own interval is in the past at the start).
    private async Task<Guid> InsertStraddlingBookingAsync(Guid resourceId, Guid userId) =>
        await InsertBookingAsync(resourceId, userId, startOffsetHours: -1, endOffsetHours: 1);

    private async Task<Guid> InsertBookingAsync(
        Guid resourceId,
        Guid userId,
        int startOffsetHours,
        int endOffsetHours)
    {
        var bookingId = Guid.NewGuid();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();

        // Whole seconds resolved once and the end derived from the start —
        // datetime2(0) *rounds* on write, and a fixture that let the two round
        // differently made an adjacency test intermittent in WP-3 Phase 4.
        command.CommandText = """
            DECLARE @now DATETIME2(0) = DATEADD(SECOND, 0, CAST(SYSUTCDATETIME() AS DATETIME2(0)));

            INSERT INTO dbo.Bookings
                (Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                 CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
            SELECT
                @BookingId, r.OrgId, r.Id, @UserId,
                DATEADD(HOUR, @StartOffsetHours, @now),
                DATEADD(HOUR, @EndOffsetHours, @now),
                1, 'Fixture booking', 'Confirmed',
                @now, @UserId, @now, @UserId
            FROM dbo.Resources AS r
            WHERE r.Id = @ResourceId;
            """;
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ResourceId", resourceId);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@StartOffsetHours", startOffsetHours);
        command.Parameters.AddWithValue("@EndOffsetHours", endOffsetHours);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        return bookingId;
    }

    private sealed record Interval(DateTime StartUtc, DateTime EndUtc);

    private static async Task<IReadOnlyList<Interval>> BookableIntervalsAsync(
        HttpClient client,
        Guid resourceId)
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

    private async Task<Guid> CreateBookableResourceAsync(
        int capacity = 4,
        bool requiresApproval = false,
        string admin = AcmeAdmin)
    {
        var client = await AuthenticatedClientAsync(admin);

        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name = $"Booking Cancel Test {Guid.NewGuid():N}",
                description = "Created by the booking cancel endpoint tests",
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

        // Open every weekday. 23:59:59 is this system's spelling of midnight
        // (decision 0022), so the day is continuous.
        var windows = Enum.GetValues<DayOfWeek>()
            .Select(day => new { weekday = day.ToString(), opensAt = "00:00:00", closesAt = "23:59:59" })
            .ToArray();

        (await client.PutAsJsonAsync(
            $"/resources/{created.Id}/availability-windows", new { windows })).EnsureSuccessStatusCode();

        if (requiresApproval)
        {
            // Approvers first, then the flag: RequiresApproval with an empty
            // approver list is refused at both ends (ApproversRequired).
            var approverEmail = admin == GlobexAdmin ? "approver@globex.test" : AcmeApprover;
            var approverId = await ScalarAsync<Guid>(
                "SELECT Id FROM dbo.Users WHERE Email = @p0;", approverEmail);

            (await client.PutAsJsonAsync(
                $"/resources/{created.Id}/approvers",
                new { approverUserIds = new[] { approverId } })).EnsureSuccessStatusCode();

            (await client.PutAsJsonAsync(
                $"/resources/{created.Id}",
                new
                {
                    name = created.Name,
                    description = "Created by the booking cancel endpoint tests",
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

    // Decision 0017's gotcha: the fixture connection needs an explicit RLS
    // bypass, or the INSERT's own SELECT (and the cleanup DELETE) silently
    // affects zero rows.
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
