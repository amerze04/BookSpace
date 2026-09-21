using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Bookings;

// WP-4 Phase 2a: GET /bookings and GET /bookings/{id} through the real pipeline
// — real middleware, real JwtBearer handler, real authorization policies, real
// SQL Server.
//
// This file settles the "view" half of the work package's fourth Week-3 task
// (FR-4.4), and it is where the owner filter is actually proved: the unit tests
// assert which BookingOwnerFilter a handler *decided on*, and only a database
// can show that the filter then excludes the rows it should. Decision 0002's
// widening for a TenantAdmin is asserted the same way.
//
// The AC-4 sweep over these two routes is here too, and it is the reason the
// 404 is not a 403: another member's booking, another tenant's booking and an id
// that exists nowhere all have to be byte-identical, which is asserted directly
// rather than inferred from three separate status codes.
//
// **Bookings are created through POST /bookings**, not decision 0017's raw-SQL
// fixture. The endpoint exists now, so the arrangement is the real path — which
// is what makes "the booking I just made appears in my list" one continuous
// piece of evidence rather than two halves that could disagree.
//
// Resources are in the UTC zone unless a test is about zones, and every test
// creates its own resource and removes it again: the host's database is shared
// across the collection and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class BookingReadEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string AcmeApprover = "approver@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string GlobexMember = "member1@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    // The second Acme account these tests need, for every "another member of the
    // same tenant" case.
    //
    // **Deliberately not member2@acme.test**, which looks like the obvious
    // choice and is unusable:
    // AuthenticationEndpointTests.Refresh_UserDeactivatedSinceLogin deactivates
    // it permanently and never restores it (its sibling's comment says so
    // outright — "that one sacrifices member2@acme.test permanently"). Using it
    // here passed in isolation and failed only in a full run, with a 401 on
    // *login*, which is exactly the from-a-distance failure that comment warns
    // shared fixtures produce.
    //
    // approver@acme.test is live, and the substitution costs nothing: the rule
    // under test is "a non-admin sees only their own bookings", and an Approver
    // is a non-admin — the Approver policy sits between Member and TenantAdmin,
    // so it is if anything the stronger choice. It is briefly deactivated by
    // Login_DeactivatedUser, which restores it in a finally, and tests within a
    // collection run sequentially, so the two cannot overlap.
    private const string AcmeColleague = AcmeApprover;

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    public BookingReadEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // A fixed future Thursday in whole seconds — datetime2(0) rounds on write
    // (CLAUDE.md §4.3), and the validator refuses fractions anyway.
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 3, 11, hour, minute, 0, DateTimeKind.Utc);

    // ---- The happy path (FR-4.4) -------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheBookingTheMemberJustCreated()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var response = await client.GetAsync($"/bookings/{created.Id}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<GetBookingQueryResponse>(
                TestJson.Options))!;

            Assert.Equal(created.Id, body.Id);
            Assert.Equal(resource, body.ResourceId);
            Assert.Equal(created.UserId, body.UserId);
            Assert.Equal(At(9), body.StartsAtUtc);
            Assert.Equal(At(10), body.EndsAtUtc);
            Assert.Equal(1, body.Quantity);
            Assert.Equal("Design review", body.Title);
            Assert.Equal(BookingStatus.Confirmed, body.Status);

            // Null until something writes them — §7's jobs and Phase 2b's cancel
            // are both outside this chunk.
            Assert.Null(body.RecurrenceRuleId);
            Assert.Null(body.CheckedInAtUtc);
            Assert.Null(body.CancelledByUserId);
            Assert.Null(body.CancelledAtUtc);
            Assert.Null(body.CancellationReason);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task List_ReturnsTheMembersOwnBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var page = await ListAsync(client, $"?resourceId={resource}");

            var row = Assert.Single(page.Items);
            Assert.Equal(created.Id, row.Id);
            Assert.Equal(1, page.TotalCount);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The one field on these responses that is not the booking's own column
    // (owner's call, 2026-09-08). A member's list spans resources, so ids alone
    // would force a fetch per row to render anything a person could read.
    [Fact]
    public async Task Both_ReadsCarryTheResourceName()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));
            var expectedName = await ScalarAsync<string>(
                "SELECT Name FROM dbo.Resources WHERE Id = @p0;", resource);

            var listed = Assert.Single((await ListAsync(client, $"?resourceId={resource}")).Items);
            Assert.Equal(expectedName, listed.ResourceName);

            var detail = await GetAsync(client, created.Id);
            Assert.Equal(expectedName, detail.ResourceName);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Phase 1c returned a 201 with no Location because the read endpoint did not
    // exist. Now it does, and the header has to actually resolve — asserted by
    // following it rather than by matching a string.
    [Fact]
    public async Task Post_NowReturnsALocationHeaderThatResolves()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            var response = await client.PostAsJsonAsync("/bookings", Booking(resource, At(9), At(10)));
            response.EnsureSuccessStatusCode();

            var location = response.Headers.Location;
            Assert.NotNull(location);

            var followed = await client.GetAsync(location);

            Assert.Equal(HttpStatusCode.OK, followed.StatusCode);

            var created = (await response.Content.ReadFromJsonAsync<CreateBookingCommandResponse>(
                TestJson.Options))!;
            var body = (await followed.Content.ReadFromJsonAsync<GetBookingQueryResponse>(
                TestJson.Options))!;

            Assert.Equal(created.Id, body.Id);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // A Pending booking is readable at its Location too — the header is honest
    // for both statuses, which is why it is not conditional on Confirmed.
    [Fact]
    public async Task Get_ReturnsAPendingBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var body = await GetAsync(client, created.Id);

            Assert.Equal(BookingStatus.Pending, body.Status);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // WP-5 Phase 3 (loose end 4): a Pending booking's detail carries the
    // ApprovalRequest an approver would act on, not just Status = Pending.
    [Fact]
    public async Task Get_CarriesTheApprovalDetailForAPendingBooking()
    {
        var resource = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var body = await GetAsync(client, created.Id);

            Assert.NotNull(body.Approval);
            Assert.Equal(ApprovalDecision.Pending, body.Approval.Decision);
            Assert.Null(body.Approval.DecidedAtUtc);
            Assert.Null(body.Approval.DecidedByUserId);
            Assert.NotEqual(Guid.Empty, body.Approval.ApprovalRequestId);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // The ordinary case — no approval gate at all — carries no Approval,
    // matching the resource's own CreateBooking response.
    [Fact]
    public async Task Get_LeavesApprovalNullWhenTheResourceNeverRequiredOne()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var body = await GetAsync(client, created.Id);

            Assert.Null(body.Approval);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // FR-3.5: an archived resource keeps its history readable. Filtering
    // archived resources out of the join would erase a member's own past
    // bookings, which is why the projection deliberately does not.
    [Fact]
    public async Task Reads_StillWorkAfterTheResourceIsArchived()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            (await admin.PostAsync($"/resources/{resource}/archive", null)).EnsureSuccessStatusCode();

            Assert.Equal(created.Id, (await GetAsync(member, created.Id)).Id);
            Assert.Single((await ListAsync(member, $"?resourceId={resource}")).Items);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- A member sees only their own (FR-4.4) -----------------------------

    [Fact]
    public async Task List_ExcludesAnotherMembersBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var owner = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(owner, resource, At(9), At(10));

            var colleague = await AuthenticatedClientAsync(AcmeColleague);
            var page = await ListAsync(colleague, $"?resourceId={resource}");

            Assert.Empty(page.Items);
            Assert.Equal(0, page.TotalCount);

            // The row exists — the colleague simply cannot see it. Asserted so a
            // filter that accidentally matched nothing at all would still fail.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM dbo.Bookings WHERE Id = @p0;", created.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Get_RefusesAnotherMembersBookingWithA404()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            // The colleague owns it and the plain member is refused, which is the
            // opposite direction to the Approver-as-reader test below. Both
            // directions matter: one proves an owner filter exists, the other
            // proves the Approver role does not lift it.
            var owner = await AuthenticatedClientAsync(AcmeColleague);
            var created = await CreateBookingAsync(owner, resource, At(9), At(10));

            var member = await AuthenticatedClientAsync(AcmeMember);
            var response = await member.GetAsync($"/bookings/{created.Id}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("BookingNotFound", await ReasonCodeAsync(response));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }
    // An Approver is a non-admin for this purpose: the Approver policy sits
    // between Member and TenantAdmin, so "only an admin may widen" has to mean
    // every non-admin, the same reasoning WP-3's non-admin tests use.
    //
    // **Still true after decision 0027's widening**, and it is the test that
    // pins what the widening is *not*: the resource here has no approvers, so
    // this Approver does not gate it and the booking stays invisible. The reach
    // is by resource, never by role alone.
    [Fact]
    public async Task Get_RefusesAnotherMembersBookingToAnApprover()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var owner = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(owner, resource, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var response = await approver.GetAsync($"/bookings/{created.Id}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // WP-7 Phase 6, decision 0027. The gap the approval queue found by being
    // clicked: an Approver could see this booking in the queue and was allowed
    // to decide on it, but opening it answered 404 — ResolveOwnerFilter had been
    // widened in WP-5 Phase 3 and ResolveDetailFilter had not.
    [Fact]
    public async Task Get_LetsAnApproverReadABookingOnAResourceTheyGate()
    {
        var gated = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, gated, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var detail = await GetAsync(approver, created.Id);

            Assert.Equal(created.Id, detail.Id);
            Assert.Equal(created.UserId, detail.UserId);
            Assert.Equal(BookingStatus.Pending, detail.Status);

            // The approval section is the reason an approver opens this screen
            // at all, so it has to survive the widened read rather than the
            // widening stopping at the booking's own columns.
            Assert.NotNull(detail.Approval);
            Assert.Equal(ApprovalDecision.Pending, detail.Approval!.Decision);
        }
        finally
        {
            await CleanUpAsync(gated);
        }
    }

    // **The regression guard for the shape of the widening.** An Approver may
    // see a booking because it is theirs *or* because it is on a resource they
    // gate — a union, not an intersection. An AND-shaped filter would have
    // passed every other test here and silently taken away an Approver's
    // ability to read their own bookings on resources they do not approve for,
    // which every plain member can do. The seeded approver owns no bookings, so
    // clicking the dev data would not have shown it either.
    [Fact]
    public async Task Get_StillLetsAnApproverReadTheirOwnBookingOnAResourceTheyDoNotGate()
    {
        var gated = await CreateBookableResourceAsync(requiresApproval: true);
        var ungated = await CreateBookableResourceAsync();

        try
        {
            // The gated resource exists only to give this Approver a non-empty
            // reach; without one the filter collapses to a plain member's and
            // the test would pass for the wrong reason.
            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var own = await CreateBookingAsync(approver, ungated, At(9), At(10));

            var detail = await GetAsync(approver, own.Id);

            Assert.Equal(own.Id, detail.Id);
        }
        finally
        {
            await CleanUpAsync(ungated);
            await CleanUpAsync(gated);
        }
    }

    // The reach widens what an Approver may read, never what they may cancel —
    // decision 0002 keeps the cancel with the owner and the TenantAdmin, and
    // FindForCancelAsync deliberately does not go through the shared owner
    // filter. Asserted because the two now sit one method apart in the same
    // repository.
    [Fact]
    public async Task Cancel_IsStillRefusedToAnApproverOnAResourceTheyGate()
    {
        var gated = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, gated, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var response = await approver.PostAsJsonAsync(
                $"/bookings/{created.Id}/cancel",
                new { reason = (string?)null });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(gated);
        }
    }

    // ---- AC-4: the three not-found cases are indistinguishable -------------

    // The reason the third case is a 404 and not a 403 (BookingNotFoundException):
    // a 403 would confirm the booking exists and leak who is holding which
    // resource. Compared body-for-body rather than status-for-status, because
    // three matching status codes with three different bodies would still leak.
    [Fact]
    public async Task Get_IsByteIdenticalForEveryInvisibleBooking()
    {
        var acmeResource = await CreateBookableResourceAsync();
        var globexResource = await CreateBookableResourceAsync(admin: GlobexAdmin);

        try
        {
            var reader = await AuthenticatedClientAsync(AcmeMember);

            var colleaguesBooking = await CreateBookingAsync(
                await AuthenticatedClientAsync(AcmeColleague), acmeResource, At(9), At(10));

            var globexBooking = await CreateBookingAsync(
                await AuthenticatedClientAsync(GlobexMember), globexResource, At(9), At(10));

            var otherMember = await reader.GetAsync($"/bookings/{colleaguesBooking.Id}");
            var otherTenant = await reader.GetAsync($"/bookings/{globexBooking.Id}");
            var nonexistent = await reader.GetAsync($"/bookings/{Guid.NewGuid()}");

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

    // Even a TenantAdmin's widening stops at the tenant boundary: AnyOwner means
    // "any owner *in this tenant*", because the query still goes through the
    // tenant-filtered DbSet (CLAUDE.md §4.2). This is the test that proves the
    // owner filter did not replace the tenant filter.
    [Fact]
    public async Task AdminScope_NeverReachesAnotherTenant()
    {
        var globexResource = await CreateBookableResourceAsync(admin: GlobexAdmin);

        try
        {
            var globexBooking = await CreateBookingAsync(
                await AuthenticatedClientAsync(GlobexMember), globexResource, At(9), At(10));

            var acmeAdmin = await AuthenticatedClientAsync(AcmeAdmin);

            var page = await ListAsync(acmeAdmin, "?scope=tenant&pageSize=100");
            Assert.DoesNotContain(page.Items, row => row.Id == globexBooking.Id);

            var response = await acmeAdmin.GetAsync($"/bookings/{globexBooking.Id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(globexResource, GlobexAdmin);
        }
    }

    // ---- The admin widening (decision 0002) --------------------------------

    [Fact]
    public async Task List_LetsAnAdminSeeAnotherMembersBookingByUserId()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var page = await ListAsync(admin, $"?resourceId={resource}&userId={created.UserId}");

            Assert.Equal(created.Id, Assert.Single(page.Items).Id);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task List_LetsAnAdminSeeTheWholeTenant()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var one = await CreateBookingAsync(
                await AuthenticatedClientAsync(AcmeMember), resource, At(9), At(10));
            var two = await CreateBookingAsync(
                await AuthenticatedClientAsync(AcmeColleague), resource, At(11), At(12));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var page = await ListAsync(admin, $"?resourceId={resource}&scope=tenant");

            Assert.Equal(2, page.TotalCount);
            Assert.Contains(page.Items, row => row.Id == one.Id);
            Assert.Contains(page.Items, row => row.Id == two.Id);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // An admin is a member of their tenant before they are its admin, so the
    // unfiltered list answers "what have *I* booked" for them too. Without this
    // the widening could have been the default and every test above would still
    // pass.
    [Fact]
    public async Task List_DefaultsAnAdminToTheirOwnBookings()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            await CreateBookingAsync(
                await AuthenticatedClientAsync(AcmeMember), resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var page = await ListAsync(admin, $"?resourceId={resource}");

            Assert.Empty(page.Items);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task Get_LetsAnAdminReadAnotherMembersBooking()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var admin = await AuthenticatedClientAsync(AcmeAdmin);
            var body = await GetAsync(admin, created.Id);

            Assert.Equal(created.Id, body.Id);
            Assert.Equal(created.UserId, body.UserId);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- A non-admin sending an admin-only parameter -----------------------

    // The settled answer (owner's call, 2026-09-08): ValidationFailed 400 with a
    // per-field error, not a 403 — ErrorKind has no Forbidden and inventing one
    // for a query-string filter is heavier than the problem — and not a quietly
    // narrowed 200, which would answer a different question than was asked.
    //
    // **AcmeApprover is refused only for `userId` here** — WP-5 Phase 3
    // (decision 0018) widened `scope=tenant` to an Approver, resource-restricted
    // rather than ignored. See List_LetsAnApproverRequestTheTenantScope below
    // for that positive case.
    [Theory]
    [InlineData(AcmeMember, "userId")]
    [InlineData(AcmeMember, "scope")]
    [InlineData(AcmeApprover, "userId")]
    public async Task List_RefusesAnAdminOnlyParameterFromANonAdmin(string email, string parameter)
    {
        var client = await AuthenticatedClientAsync(email);
        var query = parameter == "userId" ? $"?userId={Guid.NewGuid()}" : "?scope=tenant";

        var response = await client.GetAsync($"/bookings{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Read once: HttpContent's stream is single-pass, so asking for the
        // reason code and then for the body again would fail on a closed stream.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());

        // Per-field, so a client is told which parameter it may not send.
        var fields = body.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains(fields, name => name.Equals(parameter, StringComparison.OrdinalIgnoreCase));
    }

    // Refused for an admin too, who is the only caller that could send both:
    // together one of the two has to be ignored, and an accepted-then-ignored
    // parameter is exactly what the 400 above exists to avoid.
    [Fact]
    public async Task List_RefusesUserIdAndScopeTogether()
    {
        var admin = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await admin.GetAsync($"/bookings?userId={Guid.NewGuid()}&scope=tenant");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
    }

    // ---- The approver queue (WP-5 Phase 3, decision 0018) -------------------

    [Fact]
    public async Task List_LetsAnApproverSeeAPendingBookingOnTheirOwnResource()
    {
        var gated = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, gated, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var page = await ListAsync(approver, "?scope=tenant");

            var row = Assert.Single(page.Items, r => r.Id == created.Id);
            Assert.Equal(created.UserId, row.UserId);
            Assert.Equal(BookingStatus.Pending, row.Status);
        }
        finally
        {
            await CleanUpAsync(gated);
        }
    }

    // The restriction is by resource, not by status or ownership: a resource
    // this Approver is not assigned to stays invisible to them even under
    // scope=tenant, unlike a TenantAdmin's unrestricted sweep.
    [Fact]
    public async Task List_HidesBookingsOnAResourceTheApproverDoesNotApprove()
    {
        var ungated = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, ungated, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var page = await ListAsync(approver, "?scope=tenant");

            Assert.DoesNotContain(page.Items, r => r.Id == created.Id);
        }
        finally
        {
            await CleanUpAsync(ungated);
        }
    }

    // An Approver's own-scope default is untouched by the widening — still
    // exactly their own bookings, same as any other role.
    [Fact]
    public async Task List_AnApproversDefaultScopeIsStillTheirOwnBookings()
    {
        var gated = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            await CreateBookingAsync(member, gated, At(9), At(10));

            var approver = await AuthenticatedClientAsync(AcmeApprover);
            var page = await ListAsync(approver, string.Empty);

            Assert.Empty(page.Items);
        }
        finally
        {
            await CleanUpAsync(gated);
        }
    }

    // Cross-tenant isolation still holds under the widened scope: the tenant
    // filter runs before the resource restriction, never after it.
    [Fact]
    public async Task List_AnApproversTenantScopeNeverReachesAnotherTenant()
    {
        var globexGated = await CreateBookableResourceAsync(requiresApproval: true, admin: GlobexAdmin);

        try
        {
            var globexMember = await AuthenticatedClientAsync(GlobexMember);
            var created = await CreateBookingAsync(globexMember, globexGated, At(9), At(10));

            var acmeApprover = await AuthenticatedClientAsync(AcmeApprover);
            var page = await ListAsync(acmeApprover, "?scope=tenant");

            Assert.DoesNotContain(page.Items, r => r.Id == created.Id);
        }
        finally
        {
            await CleanUpAsync(globexGated, admin: GlobexAdmin);
        }
    }

    // WP-5 Phase 3 (loose end 3): the queue is the first reader of this
    // endpoint who does not already know whose booking each row is.
    [Fact]
    public async Task List_CarriesTheBookersNameOnEachRow()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var expectedName = await ScalarAsync<string>(
                "SELECT FullName FROM dbo.Users WHERE Id = @p0;", created.UserId);

            var row = (await ListAsync(member, $"?resourceId={resource}")).Items.Single();

            Assert.Equal(expectedName, row.UserName);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // WP-7 Phase 6's requested-at column — "how long has this been waiting",
    // which is what makes the approver queue a queue rather than a list. Added
    // to the list row on the owner's call (2026-09-21) rather than deferred to
    // a backend package, because the asymmetry it closes is real: this endpoint
    // already accepted `sort=createdAtUtc` (BookingSortFields) and already
    // returned the stamp on the detail read, so it would order by a field it
    // would not return, and a queue sorting oldest-first could render nothing
    // to justify the order.
    //
    // Asserted against the create response's own stamp rather than a clock
    // read here: the two must be the same instant, and a test that compared
    // against DateTime.UtcNow would pass with a rounded or re-read value.
    [Fact]
    public async Task List_CarriesTheRequestedAtStampOnEachRow()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var member = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(member, resource, At(9), At(10));

            var row = (await ListAsync(member, $"?resourceId={resource}")).Items.Single();

            Assert.Equal(created.CreatedAtUtc, row.CreatedAtUtc);

            // §4.3's second convention, and the one that fails silently: without
            // the value converter stamping DateTimeKind.Utc back on, datetime2
            // materializes as Unspecified, the JSON loses its trailing Z, and a
            // browser reads the instant as local time.
            Assert.Equal(DateTimeKind.Utc, row.CreatedAtUtc.Kind);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- The filters -------------------------------------------------------

    // Overlap, not containment: a booking that started before the window and
    // runs into it is part of what "this range" contains.
    [Theory]
    [InlineData(8, 9, false)]   // ends exactly when the booking starts — no overlap
    [InlineData(8, 10, true)]   // straddles the start
    [InlineData(9, 10, true)]   // exactly the booking
    [InlineData(9, 30, true)]   // strictly inside
    [InlineData(10, 11, false)] // starts exactly when the booking ends — no overlap
    [InlineData(11, 12, false)]
    public async Task List_FiltersByOverlappingRange(int fromHour, int toHour, bool expected)
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            await CreateBookingAsync(client, resource, At(9), At(10));

            // The "strictly inside" case above uses a half-hour window, spelled
            // as a minute offset so the theory can stay a pair of hours.
            var from = fromHour == 9 && toHour == 30 ? At(9, 15) : At(fromHour);
            var to = fromHour == 9 && toHour == 30 ? At(9, 45) : At(toHour);

            var page = await ListAsync(
                client, $"?resourceId={resource}&from={from:o}&to={to:o}");

            Assert.Equal(expected ? 1 : 0, page.TotalCount);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var confirmed = await CreateBookableResourceAsync();
        var pending = await CreateBookableResourceAsync(requiresApproval: true);

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var confirmedBooking = await CreateBookingAsync(client, confirmed, At(9), At(10));
            var pendingBooking = await CreateBookingAsync(client, pending, At(9), At(10));

            var confirmedPage = await ListAsync(client, "?status=Confirmed&pageSize=100");
            Assert.Contains(confirmedPage.Items, row => row.Id == confirmedBooking.Id);
            Assert.DoesNotContain(confirmedPage.Items, row => row.Id == pendingBooking.Id);

            var pendingPage = await ListAsync(client, "?status=Pending&pageSize=100");
            Assert.Contains(pendingPage.Items, row => row.Id == pendingBooking.Id);
            Assert.DoesNotContain(pendingPage.Items, row => row.Id == confirmedBooking.Id);
        }
        finally
        {
            await CleanUpAsync(confirmed);
            await CleanUpAsync(pending);
        }
    }

    [Fact]
    public async Task List_FiltersByResource()
    {
        var one = await CreateBookableResourceAsync();
        var two = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var onBookingOne = await CreateBookingAsync(client, one, At(9), At(10));
            await CreateBookingAsync(client, two, At(9), At(10));

            var page = await ListAsync(client, $"?resourceId={one}");

            Assert.Equal(onBookingOne.Id, Assert.Single(page.Items).Id);
        }
        finally
        {
            await CleanUpAsync(one);
            await CleanUpAsync(two);
        }
    }

    // An unknown resource id is an empty page, not a 404 — deliberately unlike
    // the blackout list, where the resource is in the route. Here it is one
    // filter among five on a collection that belongs to the member, and "no
    // bookings match" is honest for every combination of them.
    [Fact]
    public async Task List_ReturnsAnEmptyPageForAnUnknownResource()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/bookings?resourceId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await ReadPageAsync(response)).TotalCount);
    }

    // ---- Ordering and paging (decision 0015) -------------------------------

    // Chronological by default, because a booking list is read as a schedule.
    [Fact]
    public async Task List_OrdersChronologicallyByDefault()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);

            // Created out of order, so the ordering cannot be insertion order.
            var later = await CreateBookingAsync(client, resource, At(14), At(15));
            var earlier = await CreateBookingAsync(client, resource, At(9), At(10));

            var page = await ListAsync(client, $"?resourceId={resource}");

            Assert.Equal([earlier.Id, later.Id], page.Items.Select(row => row.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    [Fact]
    public async Task List_HonoursADescendingSort()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var earlier = await CreateBookingAsync(client, resource, At(9), At(10));
            var later = await CreateBookingAsync(client, resource, At(14), At(15));

            var page = await ListAsync(client, $"?resourceId={resource}&sort=-startsAtUtc");

            Assert.Equal([later.Id, earlier.Id], page.Items.Select(row => row.Id));
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Every whitelisted field has to actually order, not merely be accepted by
    // the validator — a missing arm in ApplyOrder would silently fall through to
    // the default and look like it worked.
    [Theory]
    [InlineData("startsAtUtc")]
    [InlineData("-startsAtUtc")]
    [InlineData("createdAtUtc")]
    [InlineData("-createdAtUtc")]
    [InlineData("status")]
    [InlineData("-status")]
    public async Task List_AcceptsEveryWhitelistedSort(string sort)
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/bookings?sort={sort}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task List_RefusesASortOutsideTheWhitelist()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync("/bookings?sort=userId");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
    }

    // Rejected rather than clamped, per decision 0015 — the same treatment every
    // other list endpoint gives an oversized page.
    [Fact]
    public async Task List_RefusesAnOversizedPageSize()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync("/bookings?pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_PagesWithATotalCount()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var first = await CreateBookingAsync(client, resource, At(9), At(10));
            var second = await CreateBookingAsync(client, resource, At(11), At(12));
            var third = await CreateBookingAsync(client, resource, At(13), At(14));

            var pageOne = await ListAsync(client, $"?resourceId={resource}&page=1&pageSize=2");
            Assert.Equal(3, pageOne.TotalCount);
            Assert.Equal(2, pageOne.TotalPages);
            Assert.True(pageOne.HasNextPage);
            Assert.Equal([first.Id, second.Id], pageOne.Items.Select(row => row.Id));

            var pageTwo = await ListAsync(client, $"?resourceId={resource}&page=2&pageSize=2");
            Assert.Equal(third.Id, Assert.Single(pageTwo.Items).Id);
            Assert.False(pageTwo.HasNextPage);
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // ---- The range filter's own shape rules --------------------------------

    [Fact]
    public async Task List_RefusesAnInvertedRange()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync($"/bookings?from={At(14):o}&to={At(9):o}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ValidationFailed", await ReasonCodeAsync(response));
    }

    // A silently shifted filter window is a wrong answer with no error, so a
    // zoneless instant is refused rather than guessed at.
    [Fact]
    public async Task List_RefusesAZonelessInstant()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);

        var response = await client.GetAsync("/bookings?from=2027-03-11T09:00:00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Authorization -----------------------------------------------------

    [Fact]
    public async Task Reads_RequireAuthentication()
    {
        var client = _host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/bookings")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/bookings/{Guid.NewGuid()}")).StatusCode);
    }

    // TenantMember requires the orgId claim, which decision 0012 omits for a
    // SysAdmin — PRD §2 keeps the Platform Operator out of tenant booking
    // content. So a SysAdmin token gets 403 here rather than a view of
    // everything, and this is a 403 rather than a 404 because it is RBAC
    // refusing the route, not a rule hiding a row.
    [Fact]
    public async Task Reads_RefuseASysAdmin()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/bookings")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync($"/bookings/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- Wire format -------------------------------------------------------

    // Enums serialize as names, not ordinals — Program.cs registered
    // JsonStringEnumConverter app-wide in WP-3 Phase 3, and BookingStatus is the
    // payoff that entry predicted.
    [Fact]
    public async Task Reads_SerializeTheStatusAsItsName()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var listBody = await (await client.GetAsync($"/bookings?resourceId={resource}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "Confirmed",
                listBody.GetProperty("items")[0].GetProperty("status").GetString());

            var detailBody = await (await client.GetAsync($"/bookings/{created.Id}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Confirmed", detailBody.GetProperty("status").GetString());
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Every instant carries the trailing Z. §4.3's second convention: datetime2
    // carries no offset, so without the value converter stamping DateTimeKind.Utc
    // back on, System.Text.Json would omit it and a browser would read the
    // instant as local time.
    [Fact]
    public async Task Reads_ReturnInstantsWithAUtcDesignator()
    {
        var resource = await CreateBookableResourceAsync();

        try
        {
            var client = await AuthenticatedClientAsync(AcmeMember);
            var created = await CreateBookingAsync(client, resource, At(9), At(10));

            var body = await (await client.GetAsync($"/bookings/{created.Id}"))
                .Content.ReadFromJsonAsync<JsonElement>();

            foreach (var field in new[] { "startsAtUtc", "endsAtUtc", "createdAtUtc", "updatedAtUtc" })
            {
                Assert.EndsWith("Z", body.GetProperty(field).GetString()!, StringComparison.Ordinal);
            }

            // The list row too, not only the detail — its createdAtUtc is new
            // in WP-7 Phase 6 and is read by a browser, so it has to carry the
            // designator on the wire rather than merely deserialize correctly
            // into a typed test client.
            var listBody = await (await client.GetAsync($"/bookings?resourceId={resource}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            var row = listBody.GetProperty("items")[0];

            foreach (var field in new[] { "startsAtUtc", "endsAtUtc", "createdAtUtc" })
            {
                Assert.EndsWith("Z", row.GetProperty(field).GetString()!, StringComparison.Ordinal);
            }
        }
        finally
        {
            await CleanUpAsync(resource);
        }
    }

    // Every error body is a ProblemDetails carrying the correlation id, and the
    // exception's message never reaches the client (decision 0016) — the same
    // assertion the resource and blackout suites make, extended to these routes.
    [Fact]
    public async Task Reads_ReturnProblemDetailsWithACorrelationId()
    {
        var client = await AuthenticatedClientAsync(AcmeMember);
        var bookingId = Guid.NewGuid();

        var response = await client.GetAsync($"/bookings/{bookingId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("BookingNotFound", body.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));

        // The exception says "Booking {id} was not found for the current caller";
        // none of that may be on the wire, and the id must not be echoed back in
        // a detail message either.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not found for the current caller", raw, StringComparison.Ordinal);
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

    private static async Task<PagedResult<ListBookingsQueryResponse>> ListAsync(
        HttpClient client,
        string query)
    {
        var response = await client.GetAsync($"/bookings{query}");
        response.EnsureSuccessStatusCode();

        return await ReadPageAsync(response);
    }

    private static async Task<PagedResult<ListBookingsQueryResponse>> ReadPageAsync(
        HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PagedResult<ListBookingsQueryResponse>>(
            TestJson.Options))!;

    private static async Task<GetBookingQueryResponse> GetAsync(HttpClient client, Guid bookingId)
    {
        var response = await client.GetAsync($"/bookings/{bookingId}");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GetBookingQueryResponse>(TestJson.Options))!;
    }

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
                name = $"Booking Read Test {Guid.NewGuid():N}",
                description = "Created by the booking read endpoint tests",
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
            // approver list is refused at both ends (ApproversRequired, WP-3
            // Phase 3).
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
                    description = "Created by the booking read endpoint tests",
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
    // bypass, or its SELECTs and DELETEs silently affect zero rows.
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
