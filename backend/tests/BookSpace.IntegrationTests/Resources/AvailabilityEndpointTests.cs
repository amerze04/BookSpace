using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.GetResourceAvailability;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Resources;

// WP-3 Phase 5: GET /resources/{id}/availability through the real pipeline.
//
// This file is where the work package's second acceptance criterion is settled —
// "the availability query correctly excludes blackout periods and existing
// bookings" — so blackouts go in through the real endpoint and bookings through
// decision 0017's raw-SQL fixture, because dbo.CreateBooking is WP-4 work
// (CLAUDE.md §4.1).
//
// **Most tests use a resource in the UTC zone**, deliberately. The conversion
// rules have thorough unit tests against real tzdata
// (SystemResourceTimeZoneTests), and a resource whose local time *is* UTC keeps
// these assertions about what the endpoint excludes rather than about arithmetic
// the test would have to redo to state its own expectation. The DST section at
// the end uses America/New_York on fixed dates, which is the one thing the UTC
// resource cannot show.
//
// Dates float forward from today wherever a blackout is created, because a
// blackout entirely in the past is refused (BlackoutPeriodElapsed, decision
// 0019) and a hard-coded date would start failing the moment it went by. Where no
// blackout is involved the dates are fixed, since availability has no
// future-only rule.
//
// Same state hygiene as its sibling files: every test creates its own resource
// and removes it again, because the host and its database are shared across the
// collection and other tests assert on Acme's exact resource count.
[Collection(nameof(AuthenticationTestCollection))]
public class AvailabilityEndpointTests
{
    private readonly AuthenticationTestHost _host;

    private const string AcmeAdmin = "admin@acme.test";
    private const string AcmeMember = "member1@acme.test";
    private const string GlobexAdmin = "admin@globex.test";
    private const string SysAdmin = "sysadmin@bookspace.local";

    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_AuthTests");

    public AvailabilityEndpointTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // ---- Arrangement helpers ----

    private static object Window(string weekday, string opensAt, string closesAt) =>
        new { weekday, opensAt, closesAt };

    private static string Url(Guid resourceId, DateOnly from, DateOnly to) =>
        $"/resources/{resourceId}/availability?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

    // The next Monday strictly after today, so a blackout created on it is always
    // in the future.
    private static DateOnly NextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysAhead = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;

        return today.AddDays(daysAhead == 0 ? 7 : daysAhead);
    }

    private static DateTime Utc(DateOnly date, int hour, int minute = 0) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Utc);

    // ---- The happy path ----

    [Fact]
    public async Task Get_ReturnsTheScheduleAsInstants()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Happy Path");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(resource.Id, body.ResourceId);
            Assert.Equal("UTC", body.TimeZoneId);
            Assert.Equal(monday, body.FromLocalDate);
            Assert.Equal(monday, body.ToLocalDate);
            Assert.False(body.IsArchived);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 9), interval.StartUtc);
            Assert.Equal(Utc(monday, 17), interval.EndUtc);
            Assert.Equal(4, interval.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Get_WithNoSchedule_ReturnsNoIntervals()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability No Schedule");

        try
        {
            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Empty(body.Intervals);
            Assert.False(body.IsArchived);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A member has to be able to read this — it is the endpoint the PRD's member
    // flow runs on.
    [Fact]
    public async Task Get_AsMember_IsAllowed()
    {
        var adminClient = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(adminClient, "Availability Member Read");

        try
        {
            await SetScheduleAsync(adminClient, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var memberClient = await AuthenticatedClientAsync(AcmeMember);
            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(memberClient, resource.Id, monday, monday);

            Assert.Single(body.Intervals);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Get_SpansEveryMatchingDateInTheRange()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Range");

        try
        {
            await SetScheduleAsync(
                client,
                resource.Id,
                Window("Monday", "09:00:00", "17:00:00"),
                Window("Wednesday", "09:00:00", "12:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday.AddDays(13));

            // Two Mondays and two Wednesdays, in chronological order.
            Assert.Equal(
                new[]
                {
                    (Utc(monday, 9), Utc(monday, 17)),
                    (Utc(monday.AddDays(2), 9), Utc(monday.AddDays(2), 12)),
                    (Utc(monday.AddDays(7), 9), Utc(monday.AddDays(7), 17)),
                    (Utc(monday.AddDays(9), 9), Utc(monday.AddDays(9), 12)),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Phase 3 allows adjacent windows because ClosesAt is exclusive; a member
    // asking what is bookable should see one span.
    [Fact]
    public async Task Get_JoinsAdjacentWindows()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Adjacent");

        try
        {
            await SetScheduleAsync(
                client,
                resource.Id,
                Window("Monday", "09:00:00", "12:00:00"),
                Window("Monday", "12:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 9), interval.StartUtc);
            Assert.Equal(Utc(monday, 17), interval.EndUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The midnight convention end to end: CK_AvailabilityWindows_Window forbids a
    // window crossing midnight, so an overnight resource is two rows that this
    // endpoint has to present as one span.
    [Fact]
    public async Task Get_JoinsAnOvernightScheduleStoredAsTwoWindows()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Overnight");

        try
        {
            await SetScheduleAsync(
                client,
                resource.Id,
                Window("Monday", "22:00:00", "23:59:59"),
                Window("Tuesday", "00:00:00", "02:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday.AddDays(1));

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 22), interval.StartUtc);
            Assert.Equal(Utc(monday.AddDays(1), 2), interval.EndUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Excludes blackout periods (WP-3 acceptance criterion) ----

    [Fact]
    public async Task Get_ExcludesABlackoutPeriod()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Blackout");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = NextMonday();
            await CreateBlackoutAsync(client, resource.Id, Utc(monday, 12), Utc(monday, 13));

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(
                new[]
                {
                    (Utc(monday, 9), Utc(monday, 12)),
                    (Utc(monday, 13), Utc(monday, 17)),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Get_ABlackoutCoveringTheWholeDayLeavesNothing()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Blackout All Day");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = NextMonday();
            await CreateBlackoutAsync(client, resource.Id, Utc(monday, 8), Utc(monday, 18));

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Empty(body.Intervals);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Overlapping blackouts are allowed on one resource (decision 0019), because
    // the union of two blackouts is still blacked out. Their union is what has to
    // be removed — not one of them, and not the time twice.
    [Fact]
    public async Task Get_ExcludesTheUnionOfOverlappingBlackouts()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Blackout Union");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = NextMonday();
            await CreateBlackoutAsync(client, resource.Id, Utc(monday, 11), Utc(monday, 14));
            await CreateBlackoutAsync(client, resource.Id, Utc(monday, 13), Utc(monday, 15));

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(
                new[]
                {
                    (Utc(monday, 9), Utc(monday, 11)),
                    (Utc(monday, 15), Utc(monday, 17)),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A blackout that began before the queried range still blocks the part of it
    // that reaches in — overlap, not containment.
    [Fact]
    public async Task Get_ExcludesABlackoutThatStartedBeforeTheRange()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Blackout Straddle");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = NextMonday();
            await CreateBlackoutAsync(
                client, resource.Id, Utc(monday.AddDays(-1), 12), Utc(monday, 11));

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 11), interval.StartUtc);
            Assert.Equal(Utc(monday, 17), interval.EndUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Excludes existing bookings (WP-3 acceptance criterion) ----

    // The capacity model, through the endpoint: one unit of four leaves the time
    // open with three left, rather than removing it — and for a caller wanting
    // one unit it does not even break the span.
    [Fact]
    public async Task Get_ABookingLowersTheFloorWithoutRemovingTheTime()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Booking Partial");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(resource.Id, Utc(monday, 12), Utc(monday, 13), quantity: 1);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(1, body.Quantity);
            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 9), interval.StartUtc);
            Assert.Equal(Utc(monday, 17), interval.EndUtc);
            Assert.Equal(3, interval.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The same day and the same booking, asked for all four units: now the booked
    // hour is a wall. This pair is why `quantity` exists — one list of intervals
    // cannot answer both questions.
    [Fact]
    public async Task Get_WithAQuantity_TreatsAPartialBookingAsAWall()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Booking Quantity");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(resource.Id, Utc(monday, 12), Utc(monday, 13), quantity: 1);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday, quantity: 4);

            Assert.Equal(4, body.Quantity);
            Assert.Equal(
                new[]
                {
                    (Utc(monday, 9), Utc(monday, 12), 4),
                    (Utc(monday, 13), Utc(monday, 17), 4),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc, i.RemainingCapacity)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Asking for more units than the resource has is a true "nothing", not an
    // error: the validator cannot see the capacity, and an empty list is the
    // honest answer.
    [Fact]
    public async Task Get_WithAQuantityAboveCapacity_ReturnsNoIntervals()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Quantity Too Big");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday, quantity: 5);

            Assert.Empty(body.Intervals);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Get_WithAQuantityOfZero_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var monday = new DateOnly(2026, 9, 7);

        var response = await client.GetAsync(
            $"{Url(Guid.NewGuid(), monday, monday)}&quantity=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReasonCodeAsync(response, "ValidationFailed");
    }

    [Fact]
    public async Task Get_ABookingTakingEveryUnitRemovesTheTime()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Booking Full");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(resource.Id, Utc(monday, 12), Utc(monday, 13), quantity: 4);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(
                new[]
                {
                    (Utc(monday, 9), Utc(monday, 12)),
                    (Utc(monday, 13), Utc(monday, 17)),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Get_ConcurrentBookingsSumAgainstCapacity()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Booking Concurrent");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(resource.Id, Utc(monday, 10), Utc(monday, 14), quantity: 1);
            await InsertBookingAsync(resource.Id, Utc(monday, 12), Utc(monday, 13), quantity: 2);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(
                new[]
                {
                    // One unit is free all day — the tightest hour has exactly
                    // one — so a caller wanting one unit sees one span, and the
                    // figure is the floor across it.
                    (Utc(monday, 9), Utc(monday, 17), 1),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc, i.RemainingCapacity)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // Only Pending and Confirmed hold units — the same live set decision 0005's
    // capacity arithmetic uses everywhere else. A cancelled booking holds nothing,
    // which is the half a naive "any row on this resource" query would get wrong.
    [Theory]
    [InlineData("Pending", 3)]
    [InlineData("Confirmed", 3)]
    [InlineData("Cancelled", 4)]
    [InlineData("Rejected", 4)]
    [InlineData("NoShow", 4)]
    [InlineData("Completed", 4)]
    public async Task Get_OnlyLiveBookingsConsumeCapacity(string status, int expectedRemaining)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, $"Availability Booking {status}");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(
                resource.Id, Utc(monday, 9), Utc(monday, 17), quantity: 1, status: status);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(expectedRemaining, interval.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A booking that began before the queried range still holds its units inside
    // it. Overlap, not containment — the same rule as the blackout case, and the
    // one a range-bounded fetch is most likely to get wrong.
    [Fact]
    public async Task Get_ABookingStartingBeforeTheRangeStillHoldsItsUnits()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Booking Straddle");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(
                resource.Id, Utc(monday.AddDays(-1), 20), Utc(monday, 11), quantity: 2);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(
                new[]
                {
                    // Two of four units are held until 11:00, which lowers the
                    // day's floor without breaking the span.
                    (Utc(monday, 9), Utc(monday, 17), 2),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc, i.RemainingCapacity)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // A booking on someone else's resource must not touch this one's answer. The
    // repository filters by ResourceId as well as by tenant, and this is the test
    // that would fail if it stopped doing so.
    [Fact]
    public async Task Get_IgnoresBookingsOnAnotherResource()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Booking Own");
        var other = await CreateResourceAsync(client, "Availability Booking Other");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            await InsertBookingAsync(other.Id, Utc(monday, 12), Utc(monday, 13), quantity: 4);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(4, interval.RemainingCapacity);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
            await CleanUpAsync(other.Id);
        }
    }

    // Both exclusions at once, which is the acceptance criterion as written.
    [Fact]
    public async Task Get_ExcludesBlackoutsAndBookingsTogether()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Both");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = NextMonday();
            await CreateBlackoutAsync(client, resource.Id, Utc(monday, 15), Utc(monday, 16));
            await InsertBookingAsync(resource.Id, Utc(monday, 10), Utc(monday, 11), quantity: 1);

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.Equal(
                new[]
                {
                    // The blackout is a hard split; the booking only lowers the
                    // first half's floor.
                    (Utc(monday, 9), Utc(monday, 15), 3),
                    (Utc(monday, 16), Utc(monday, 17), 4),
                },
                body.Intervals.Select(i => (i.StartUtc, i.EndUtc, i.RemainingCapacity)));
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- The minimum-duration floor ----

    [Fact]
    public async Task Get_DropsASpanShorterThanTheResourcesMinimumDuration()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(
            client, "Availability Min Duration", minDurationMinutes: 120);

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = NextMonday();

            // Leaves 09:00-09:30 in front of the blackout — under the two-hour
            // floor — and 14:00-17:00 behind it.
            await CreateBlackoutAsync(client, resource.Id, Utc(monday, 9, 30), Utc(monday, 14));

            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 14), interval.StartUtc);
            Assert.Equal(Utc(monday, 17), interval.EndUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- An archived resource (owner's call, 2026-09-03) ----

    // 200 with an empty list, not 422 ResourceArchived: FR-3.5 keeps an archived
    // resource readable and "nothing is bookable" is the true answer. isArchived
    // is what tells that apart from a resource that simply never opens.
    [Fact]
    public async Task Get_AnArchivedResource_Returns200WithNoIntervalsAndTheFlagSet()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Archived");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var archive = await client.PostAsync($"/resources/{resource.Id}/archive", content: null);
            archive.EnsureSuccessStatusCode();

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            Assert.True(body.IsArchived);
            Assert.Empty(body.Intervals);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Not found, and the cross-tenant case (AC-4) ----

    [Fact]
    public async Task Get_AnUnknownResource_Returns404()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var monday = new DateOnly(2026, 9, 7);

        var response = await client.GetAsync(Url(Guid.NewGuid(), monday, monday));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertReasonCodeAsync(response, "ResourceNotFound");
    }

    // The important one. A real id from another tenant must be indistinguishable
    // from one that exists nowhere — confirming it exists is the leak AC-4
    // forbids.
    [Fact]
    public async Task Get_AnotherTenantsResource_IsIndistinguishableFromOneThatDoesNotExist()
    {
        var globexClient = await AuthenticatedClientAsync(GlobexAdmin);
        var globexResource = await CreateResourceAsync(globexClient, "Availability Globex");

        try
        {
            var acmeClient = await AuthenticatedClientAsync(AcmeMember);
            var monday = new DateOnly(2026, 9, 7);

            var crossTenant = await acmeClient.GetAsync(Url(globexResource.Id, monday, monday));
            var nonexistent = await acmeClient.GetAsync(Url(Guid.NewGuid(), monday, monday));

            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
            await AssertIdenticalProblemAsync(crossTenant, nonexistent);
        }
        finally
        {
            await CleanUpAsync(globexResource.Id);
        }
    }

    // ---- Authorization ----

    [Fact]
    public async Task Get_WithoutAToken_Returns401()
    {
        var client = _host.CreateClient();
        var monday = new DateOnly(2026, 9, 7);

        var response = await client.GetAsync(Url(Guid.NewGuid(), monday, monday));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // TenantMember requires the orgId claim, which decision 0012 deliberately
    // omits for a SysAdmin (PRD §2: the Platform Operator must never see tenant
    // booking content in routine operation).
    [Fact]
    public async Task Get_AsSysAdmin_Returns403()
    {
        var client = await AuthenticatedClientAsync(SysAdmin);
        var monday = new DateOnly(2026, 9, 7);

        var response = await client.GetAsync(Url(Guid.NewGuid(), monday, monday));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Request shape: 400s, all ValidationFailed rather than a new code ----

    [Fact]
    public async Task Get_AnInvertedRange_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var monday = new DateOnly(2026, 9, 7);

        var response = await client.GetAsync(Url(Guid.NewGuid(), monday, monday.AddDays(-1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReasonCodeAsync(response, "ValidationFailed");
    }

    [Fact]
    public async Task Get_ARangeLongerThanNinetyDays_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var monday = new DateOnly(2026, 9, 7);

        var response = await client.GetAsync(Url(Guid.NewGuid(), monday, monday.AddDays(90)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReasonCodeAsync(response, "ValidationFailed");
    }

    // Refused before the resource is looked up, so an over-long range on an
    // unknown resource is a 400 rather than a 404 — the request is malformed
    // whatever it points at.
    [Fact]
    public async Task Get_ExactlyNinetyDays_IsAccepted()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Ninety Days");

        try
        {
            var monday = new DateOnly(2026, 9, 7);
            var response = await client.GetAsync(Url(resource.Id, monday, monday.AddDays(89)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    [Fact]
    public async Task Get_WithNoDates_Returns400()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);

        var response = await client.GetAsync($"/resources/{Guid.NewGuid()}/availability");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReasonCodeAsync(response, "ValidationFailed");
    }

    // ---- Daylight saving, on a real zone (WP-3 decision D3) ----

    // The one thing the UTC resource cannot show. Fixed dates, because these are
    // the published 2026 transitions for America/New_York — 03-08 forward, 11-01
    // back — and no blackout is involved, so nothing here needs to be in the
    // future.
    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public async Task Get_ADaylightSavingTransitionMakesTheLocalDayShorterOrLonger(
        int year, int month, int day, int expectedHours)
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(
            client, $"Availability DST {year}-{month}-{day}", timeZoneId: "America/New_York");

        try
        {
            var date = new DateOnly(year, month, day);

            // "All day", both times: 00:00 to the end of the day.
            await SetScheduleAsync(
                client, resource.Id, Window(date.DayOfWeek.ToString(), "00:00:00", "23:59:59"));

            var body = await GetAvailabilityAsync(client, resource.Id, date, date);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(TimeSpan.FromHours(expectedHours), interval.EndUtc - interval.StartUtc);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // The ordinary case in a real zone, so the offset is proved to be applied at
    // all: 09:00-17:00 local on a September Monday in New York is 13:00Z-21:00Z.
    [Fact]
    public async Task Get_AppliesTheResourcesOwnZoneOffset()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(
            client, "Availability Zone Offset", timeZoneId: "America/New_York");

        try
        {
            await SetScheduleAsync(client, resource.Id, Window("Monday", "09:00:00", "17:00:00"));

            var monday = new DateOnly(2026, 9, 7);
            var body = await GetAvailabilityAsync(client, resource.Id, monday, monday);

            var interval = Assert.Single(body.Intervals);
            Assert.Equal(Utc(monday, 13), interval.StartUtc);
            Assert.Equal(Utc(monday, 21), interval.EndUtc);
            Assert.Equal("America/New_York", body.TimeZoneId);
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- The PRD's only endpoint-specific NFR ----

    // "Availability queries and calendar views remain responsive with realistic
    // data volumes (hundreds of bookings per resource)." A deliberate check
    // rather than an assumption, per docs/wp3-plan.md's risk list.
    //
    // The bound is loose on purpose: this asserts the shape of the cost — three
    // queries and an in-memory sweep, so a full 90-day range over 300 bookings
    // stays well inside a second — not a benchmark. A regression that made the
    // sweep quadratic, or fetched per date, would blow through it; ordinary
    // machine noise will not.
    [Fact]
    public async Task Get_StaysResponsiveOverNinetyDaysWithHundredsOfBookings()
    {
        var client = await AuthenticatedClientAsync(AcmeAdmin);
        var resource = await CreateResourceAsync(client, "Availability Volume", capacity: 10);

        try
        {
            await SetScheduleAsync(
                client,
                resource.Id,
                Window("Monday", "09:00:00", "17:00:00"),
                Window("Tuesday", "09:00:00", "17:00:00"),
                Window("Wednesday", "09:00:00", "17:00:00"),
                Window("Thursday", "09:00:00", "17:00:00"),
                Window("Friday", "09:00:00", "17:00:00"));

            var from = new DateOnly(2026, 9, 7);
            await InsertBookingsAsync(resource.Id, from, days: 90, perDay: 4);

            var stopwatch = Stopwatch.StartNew();
            var body = await GetAvailabilityAsync(client, resource.Id, from, from.AddDays(89));
            stopwatch.Stop();

            // 90 days is 64 or 65 weekdays, each carrying four bookings, so the
            // answer is real work rather than an empty list timed quickly. Both
            // assertions matter: the interval count proves the sweep really split
            // every day at its four booking boundaries, and the presence of a
            // reduced figure proves the fixture's rows were actually counted —
            // without it this would pass just as happily if the inserts had
            // silently affected zero rows, which is exactly the failure decision
            // 0017 warns about.
            var weekdays = Enumerable.Range(0, 90)
                .Select(from.AddDays)
                .Count(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));

            // At one unit, every hour of every weekday can take the booking, so
            // each day is one interval — carrying 9 rather than 10, which is what
            // proves the fixture's rows were actually counted. Without that
            // second assertion this would pass just as happily if the inserts had
            // silently affected zero rows, the exact failure decision 0017 warns
            // about.
            Assert.Equal(weekdays, body.Intervals.Count);
            Assert.All(body.Intervals, i => Assert.Equal(9, i.RemainingCapacity));
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Availability over 90 days took {stopwatch.ElapsedMilliseconds} ms.");

            // The same range needing every unit, which is strictly more work: the
            // four bookings each become a wall, leaving the four free hours
            // between and after them. This is where the sweep is actually
            // exercised — 260 bookings turned into 1,040 intervals.
            var everyUnit = Stopwatch.StartNew();
            var exclusive = await GetAvailabilityAsync(
                client, resource.Id, from, from.AddDays(89), quantity: 10);
            everyUnit.Stop();

            Assert.Equal(weekdays * 4, exclusive.Intervals.Count);
            Assert.All(exclusive.Intervals, i => Assert.Equal(10, i.RemainingCapacity));
            Assert.True(
                everyUnit.Elapsed < TimeSpan.FromSeconds(5),
                $"Availability at full quantity took {everyUnit.ElapsedMilliseconds} ms.");
        }
        finally
        {
            await CleanUpAsync(resource.Id);
        }
    }

    // ---- Helpers ----

    private static async Task<GetResourceAvailabilityQueryResponse> GetAvailabilityAsync(
        HttpClient client,
        Guid resourceId,
        DateOnly from,
        DateOnly to,
        int? quantity = null)
    {
        // Omitted rather than sent as 1 when the caller does not care, so the
        // endpoint's own default is what most of these tests exercise.
        var url = Url(resourceId, from, to) + (quantity is null ? string.Empty : $"&quantity={quantity}");
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GetResourceAvailabilityQueryResponse>(
            TestJson.Options))!;
    }

    // UTC unless a test says otherwise: see the class header for why most of these
    // deliberately avoid a real zone. Capacity 4 and no duration floor, so a test
    // that cares about either states it.
    private async Task<CreateResourceCommandResponse> CreateResourceAsync(
        HttpClient client,
        string name,
        string timeZoneId = "UTC",
        int capacity = 4,
        int? minDurationMinutes = null)
    {
        var response = await client.PostAsJsonAsync(
            "/resources",
            new
            {
                name,
                description = "Created by the availability tests",
                resourceType = "Room",
                capacity,
                timeZoneId,
                requiresApproval = false,
                minDurationMinutes,
                maxDurationMinutes = (int?)null,
            });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreateResourceCommandResponse>(TestJson.Options))!;
    }

    private static async Task SetScheduleAsync(HttpClient client, Guid resourceId, params object[] windows)
    {
        var response = await client.PutAsJsonAsync(
            $"/resources/{resourceId}/availability-windows", new { windows });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateBlackoutAsync(
        HttpClient client,
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc)
    {
        var response = await client.PostAsJsonAsync(
            $"/resources/{resourceId}/blackout-periods",
            new
            {
                startsAtUtc = startsAtUtc.ToString("o"),
                endsAtUtc = endsAtUtc.ToString("o"),
                reason = "Availability test",
            });
        response.EnsureSuccessStatusCode();
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

    // Byte-identical apart from the per-request correlation id and trace id, which
    // are the only fields allowed to differ.
    private static async Task AssertIdenticalProblemAsync(
        HttpResponseMessage first,
        HttpResponseMessage second)
    {
        var one = await Redacted(first);
        var two = await Redacted(second);

        Assert.Equal(one, two);

        static async Task<string> Redacted(HttpResponseMessage response)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var fields = body.EnumerateObject()
                .Where(p => p.Name is not ("correlationId" or "traceId"))
                .Select(p => $"{p.Name}={p.Value.GetRawText()}")
                .OrderBy(text => text, StringComparer.Ordinal);

            return string.Join("|", fields);
        }
    }

    // Decision 0017's carve-out from CLAUDE.md §4.1: a fixture may insert Bookings
    // rows with raw SQL, never LINQ and never SaveChanges, and only in a fixture.
    // dbo.CreateBooking is WP-4 work, so there is no production write path to
    // build this input through yet, and raw SQL cannot be mistaken for one.
    //
    // Quantity is a parameter here, unlike the blackout tests' sibling helper
    // which hard-codes 1: partial consumption of capacity is the whole point of
    // decision 0005 and half of what this file has to prove.
    //
    // The instants are passed in whole seconds by every caller (Utc builds them
    // that way). That matters: StartsAtUtc/EndsAtUtc are datetime2(0), which
    // *rounds* on write (CLAUDE.md §4.3), so a fractional value would move the
    // stored boundary off the one the assertion expects.
    //
    // The RLS bypass is essential rather than convenient — the INSERT's own SELECT
    // over Resources and Users returns zero rows without it, and the statement
    // then inserts nothing and reports no error.
    private static async Task InsertBookingAsync(
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int quantity,
        string status = "Confirmed")
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.Bookings
                (Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                 CreatedAtUtc, CreatedByUserId, UpdatedAtUtc)
            SELECT NEWID(), r.OrgId, r.Id, u.Id, @StartsAtUtc, @EndsAtUtc, @Quantity, @Title, @Status,
                   SYSUTCDATETIME(), u.Id, SYSUTCDATETIME()
            FROM dbo.Resources r
            CROSS JOIN (SELECT TOP 1 Id FROM dbo.Users WHERE Email = @Email) u
            WHERE r.Id = @ResourceId;
            """;
        command.Parameters.AddWithValue("@ResourceId", resourceId);
        command.Parameters.AddWithValue("@Email", AcmeMember);
        command.Parameters.AddWithValue("@StartsAtUtc", startsAtUtc);
        command.Parameters.AddWithValue("@EndsAtUtc", endsAtUtc);
        command.Parameters.AddWithValue("@Quantity", quantity);
        command.Parameters.AddWithValue("@Title", "Availability fixture");
        command.Parameters.AddWithValue("@Status", status);

        var inserted = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, inserted);
    }

    // The volume fixture, in one round trip per day rather than one per booking —
    // 360 separate connections would dominate the test's runtime and prove
    // nothing about the endpoint.
    //
    // Bookings land inside the 09:00-17:00 window on weekdays only, so they
    // actually consume advertised availability instead of falling outside it.
    private static async Task InsertBookingsAsync(
        Guid resourceId,
        DateOnly from,
        int days,
        int perDay)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await EnterRlsBypassAsync(connection);

        for (var offset = 0; offset < days; offset++)
        {
            var date = from.AddDays(offset);

            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            var values = string.Join(
                ",\n",
                Enumerable.Range(0, perDay).Select(slot =>
                    $"(NEWID(), r.OrgId, r.Id, u.Id, @Start{slot}, @End{slot}, 1, 'Volume fixture', " +
                    "'Confirmed', SYSUTCDATETIME(), u.Id, SYSUTCDATETIME())"));

            // Written as a VALUES list over the same CROSS JOIN the single-row
            // helper uses, so it is still the fixture's raw SQL and still needs no
            // production write path.
            command.CommandText = $"""
                INSERT INTO dbo.Bookings
                    (Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                     CreatedAtUtc, CreatedByUserId, UpdatedAtUtc)
                SELECT v.*
                FROM dbo.Resources r
                CROSS JOIN (SELECT TOP 1 Id FROM dbo.Users WHERE Email = @Email) u
                CROSS APPLY (VALUES
                {values}
                ) AS v(Id, OrgId, ResourceId, UserId, StartsAtUtc, EndsAtUtc, Quantity, Title, Status,
                       CreatedAtUtc, CreatedByUserId, UpdatedAtUtc)
                WHERE r.Id = @ResourceId;
                """;

            command.Parameters.AddWithValue("@ResourceId", resourceId);
            command.Parameters.AddWithValue("@Email", AcmeMember);

            for (var slot = 0; slot < perDay; slot++)
            {
                command.Parameters.AddWithValue($"@Start{slot}", Utc(date, 9 + slot * 2));
                command.Parameters.AddWithValue($"@End{slot}", Utc(date, 10 + slot * 2));
            }

            await command.ExecuteNonQueryAsync();
        }
    }

    // Order matters. Notifications reference Bookings with ON DELETE CASCADE, so
    // they go with the bookings; Bookings reference Resources with NoAction
    // (CLAUDE.md §4.5), so the resource cannot go while a booking references it.
    // Blackouts cascade with the resource.
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
    // predicate applies to DELETE as well as SELECT, so without it this deletes
    // zero rows and reports success — leaving bookings behind that then block the
    // resource delete.
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
    // deliberately, TenantBypass says it is scopeless on purpose.
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
