using BookSpace.Application.Abstractions;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// WP-1 seed data: a realistic multi-tenant dataset.
//
// **It now reaches Bookings** (WP-4 Phase 3). It stopped short of them from WP-1
// until 2026-09-08 for a specific reason rather than for lack of time: CLAUDE.md
// §4.1 requires every Booking write to go through dbo.CreateBooking, which did
// not exist, so there was nothing legitimate to seed. It exists now, so the
// dataset finally contains what the booking engine produces — see
// SeedBookingsAsync, which goes through the same procedure and therefore obeys
// the same capacity and blackout rules a real request does.
//
// Still skipped, and each for its own reason: **Notifications**, because nothing
// dispatches them yet (CLAUDE.md §7) and rows a future job would read as unsent
// mail are a trap rather than realism; **RefreshTokens**, because they are issued
// at login and are not meaningful as static data; and **Bookings for the
// recurrence rule below**, because materializing a series is WP-5's.
public static class SeedData
{
    // Development only — see the comment at its use site.
    public const string SeedPassword = "Passw0rd!";

    public static async Task SeedAsync(
        BookSpaceDbContext context,
        IPasswordHasher passwordHasher,
        IBookingRepository bookings,
        ITimeZoneCatalog timeZones)
    {
        if (await context.Organizations.AnyAsync())
            return;

        // Truncated to whole seconds, the convention CLAUDE.md §4.3 records for
        // IClock: every instant column here is datetime2(0), which *rounds* on
        // write, so an untruncated stamp is stored as a different value than the
        // one the seeded entity holds in memory. Harmless until something
        // compares the two — which is exactly the trap that made a WP-3
        // adjacency test intermittent.
        var now = Truncate(DateTime.UtcNow);

        // Every seeded account shares one known development password so the auth
        // flow is exercisable straight after a migrate + seed. Hashed once —
        // PBKDF2 at 100k iterations nine times over is needless work, and these
        // are all the same input anyway. Documented in the README; this is dev
        // seed data and has no place in a real environment.
        var seedPasswordHash = passwordHasher.Hash(SeedPassword);

        // Bootstrap SysAdmin — no self-registration (see the audit-trail
        // convention comment in docs/bookspace-schema-v2.sql), so the very
        // first user self-references its own Id.
        var sysAdminId = Guid.NewGuid();
        var sysAdmin = new User(
            sysAdminId,
            orgId: null,
            email: "sysadmin@bookspace.local",
            passwordHash: seedPasswordHash,
            fullName: "System Administrator",
            createdByUserId: sysAdminId,
            nowUtc: now);
        sysAdmin.AddRole(Role.SysAdmin, sysAdminId, now);
        context.Users.Add(sysAdmin);

        var acme = CreateOrg(context, "Acme Corporation", "acme", "America/New_York", 60, 15, 24, sysAdminId, now);
        var globex = CreateOrg(context, "Globex Corporation", "globex", "Europe/Berlin", 30, 10, 48, sysAdminId, now);

        var tenants = new[]
        {
            SeedTenant(context, acme, "acme.test", now, seedPasswordHash),
            SeedTenant(context, globex, "globex.test", now, seedPasswordHash),
        };

        await context.SaveChangesAsync();

        // After the save, not before: dbo.CreateBooking reads the resource row
        // and would answer ResourceNotFound for one that exists only in the
        // change tracker.
        await SeedBookingsAsync(context, bookings, timeZones, tenants, now);
    }

    private static DateTime Truncate(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);

    private static Organization CreateOrg(
        BookSpaceDbContext context,
        string name,
        string slug,
        string timeZoneId,
        int reminderLeadMinutes,
        int noShowGraceMinutes,
        int approvalExpiryHours,
        Guid createdByUserId,
        DateTime now)
    {
        var org = new Organization(
            Guid.NewGuid(), name, slug, timeZoneId,
            reminderLeadMinutes, noShowGraceMinutes, approvalExpiryHours, createdByUserId, now);
        context.Organizations.Add(org);
        return org;
    }

    // What one tenant's seeding produced, so SeedBookingsAsync can book against
    // it without re-querying rows this method already has in hand. Returned
    // rather than looked up again because a lookup would need both an
    // IgnoreQueryFilters and an RLS bypass to find rows that were just written.
    private sealed record SeededTenant(
        Organization Org,
        User MemberOne,
        User MemberTwo,
        Resource OpenResource,
        Resource ApprovedResource,
        BlackoutPeriod Blackout);

    private static SeededTenant SeedTenant(
        BookSpaceDbContext context, Organization org, string domain, DateTime now, string passwordHash)
    {
        // Provisioned by the SysAdmin (no self-registration, CLAUDE.md §1).
        var tenantAdmin = new User(Guid.NewGuid(), org.Id, $"admin@{domain}", passwordHash, "Tenant Admin", org.CreatedByUserId, now);
        tenantAdmin.AddRole(Role.TenantAdmin, org.CreatedByUserId, now);
        context.Users.Add(tenantAdmin);

        // Everyone else provisioned by the TenantAdmin from here on.
        var approver = new User(Guid.NewGuid(), org.Id, $"approver@{domain}", passwordHash, "Resource Approver", tenantAdmin.Id, now);
        approver.AddRole(Role.Approver, tenantAdmin.Id, now);
        context.Users.Add(approver);

        var memberOne = new User(Guid.NewGuid(), org.Id, $"member1@{domain}", passwordHash, "Member One", tenantAdmin.Id, now);
        memberOne.AddRole(Role.Member, tenantAdmin.Id, now);
        context.Users.Add(memberOne);

        var memberTwo = new User(Guid.NewGuid(), org.Id, $"member2@{domain}", passwordHash, "Member Two", tenantAdmin.Id, now);
        memberTwo.AddRole(Role.Member, tenantAdmin.Id, now);
        context.Users.Add(memberTwo);

        // Capacity 1, not 8, and the correction is the point rather than a
        // tidy-up. Capacity counts *concurrent units*
        // (docs/decisions/0005-capacity-semantics.md), so the original 8 said
        // "eight simultaneous bookings of this one room" when it plainly meant
        // eight seats — the exact reading 0005 exists to forbid, sitting in the
        // dataset the whole project demos from. One room is one unit; how many
        // people fit in it is not something this system models.
        var openResource = new Resource(
            Guid.NewGuid(), org.Id, "Conference Room A", ResourceType.Room,
            capacity: 1, timeZoneId: org.TimeZoneId, requiresApproval: false,
            minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main conference room", createdByUserId: tenantAdmin.Id, nowUtc: now);
        AddWeekdayWindows(openResource, tenantAdmin.Id, now);
        context.Resources.Add(openResource);

        // Capacity 1 is right here and always was: one printer, one job at a time.
        var approvedResource = new Resource(
            Guid.NewGuid(), org.Id, "3D Printer", ResourceType.Equipment,
            capacity: 1, timeZoneId: org.TimeZoneId, requiresApproval: true,
            minDurationMinutes: 60, maxDurationMinutes: 180,
            description: "Shared prototyping printer", createdByUserId: tenantAdmin.Id, nowUtc: now);
        approvedResource.AddApprover(approver.Id, tenantAdmin.Id, now);
        AddWeekdayWindows(approvedResource, tenantAdmin.Id, now);
        context.Resources.Add(approvedResource);

        var blackout = new BlackoutPeriod(
            Guid.NewGuid(), openResource.OrgId, openResource.Id,
            startsAtUtc: new DateTime(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2026, 12, 26, 0, 0, 0, DateTimeKind.Utc),
            reason: "Public holiday", createdByUserId: tenantAdmin.Id, nowUtc: now);
        context.BlackoutPeriods.Add(blackout);

        // Demonstrates the model's 2-year cap (Decision #7 / CLAUDE.md §12
        // WP-1 AC) — a weekly standup running exactly to the boundary. No
        // Bookings are materialized for it; that requires dbo.CreateBooking,
        // which doesn't exist yet (CLAUDE.md §4.1).
        var standupStart = new DateOnly(2026, 8, 24);
        var recurrenceRule = new RecurrenceRule(
            Guid.NewGuid(), org.Id, openResource.Id, memberOne.Id,
            RecurrenceFrequency.Weekly, intervalValue: 1,
            localStartTime: new TimeOnly(9, 0), localEndTime: new TimeOnly(9, 30),
            startDate: standupStart, endDate: standupStart.AddYears(2), occurrenceCount: null,
            timeZoneId: org.TimeZoneId, createdByUserId: memberOne.Id, nowUtc: now);
        context.RecurrenceRules.Add(recurrenceRule);

        return new SeededTenant(org, memberOne, memberTwo, openResource, approvedResource, blackout);
    }

    // ---- Bookings (WP-4 Phase 3) -------------------------------------------

    // Three bookings per tenant, through dbo.CreateBooking like every other
    // write (CLAUDE.md §4.1): two Confirmed on the conference room and one
    // Pending on the approval-gated printer, the last with the ApprovalRequest
    // row an approver decides — so WP-5's queue has something in it on the day
    // it is built.
    //
    // **The tenant session context, and why a bypass is the right one here.**
    // The procedure reads Resources through the RLS-filtered table so that a
    // connection with no tenant context fails closed rather than counting zero
    // overlapping bookings and overbooking (decision 0023's fail-open guard).
    // Seeding has no HttpContext, so ICurrentTenant.OrgId is null — but a bypass
    // is *not* that failure case: TenantBypassScope sets TenantInit = 1 as well,
    // and Security.fn_TenantAccessPredicate allows every row when
    // TenantBypass = 1, so the resource is visible and the guard is satisfied
    // honestly rather than dodged. The capacity count stays correct under the
    // bypass because it filters by ResourceId, and FK_Bookings_Resources_SameOrg
    // (decision 0006) makes every booking of a resource physically unable to
    // belong to another org — so there is nothing cross-tenant for a widened
    // read to over-count.
    //
    // The connection is opened **inside** the scope deliberately:
    // TenantSessionContextInterceptor reads the flag at ConnectionOpened and
    // sets its keys @read_only = 1, so a connection opened before the scope
    // could not be corrected inside it. Held open across all six bookings for
    // the same reason.
    private static async Task SeedBookingsAsync(
        BookSpaceDbContext context,
        IBookingRepository bookings,
        ITimeZoneCatalog timeZones,
        IReadOnlyList<SeededTenant> tenants,
        DateTime nowUtc)
    {
        using var bypass = TenantBypassScope.Enter();
        await context.Database.OpenConnectionAsync();

        try
        {
            foreach (var tenant in tenants)
            {
                var zone = timeZones.GetResourceTimeZone(tenant.Org.TimeZoneId);
                var date = NextBookableLocalDate(zone, nowUtc, tenant.Blackout);

                await CreateAsync(
                    bookings, tenant.OpenResource, tenant.MemberOne.Id, zone, date,
                    new TimeOnly(10, 0), new TimeOnly(11, 0),
                    BookingStatus.Confirmed, "Weekly planning", nowUtc);

                await CreateAsync(
                    bookings, tenant.OpenResource, tenant.MemberTwo.Id, zone, date,
                    new TimeOnly(14, 0), new TimeOnly(15, 0),
                    BookingStatus.Confirmed, "Client call", nowUtc);

                var pendingId = await CreateAsync(
                    bookings, tenant.ApprovedResource, tenant.MemberOne.Id, zone, date,
                    new TimeOnly(11, 0), new TimeOnly(13, 0),
                    BookingStatus.Pending, "Prototype print", nowUtc);

                // FR-7.4: a null ApprovalExpiryHours means the tenant set no
                // expiry and the request waits indefinitely, which is the same
                // reading CreateBookingCommandRequestHandler takes.
                context.ApprovalRequests.Add(new ApprovalRequest(
                    Guid.NewGuid(),
                    pendingId,
                    nowUtc,
                    tenant.Org.ApprovalExpiryHours is { } hours ? nowUtc.AddHours(hours) : null));
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<Guid> CreateAsync(
        IBookingRepository bookings,
        Resource resource,
        Guid userId,
        IResourceTimeZone zone,
        DateOnly localDate,
        TimeOnly localStart,
        TimeOnly localEnd,
        BookingStatus status,
        string title,
        DateTime nowUtc)
    {
        var bookingId = Guid.NewGuid();

        // Earliest for the start and latest for the end — IResourceTimeZone's
        // stated convention for an interval, so a booking spanning a clocks-back
        // hour is the 25 hours that actually elapsed rather than quietly an hour
        // short (WP-3 decision D3, decision 0021).
        var outcome = await bookings.CreateAsync(
            new NewBooking(
                bookingId,
                resource.Id,
                userId,
                RecurrenceRuleId: null,
                zone.ToUtcEarliest(localDate.ToDateTime(localStart)),
                zone.ToUtcLatest(localDate.ToDateTime(localEnd)),
                Quantity: 1,
                title,
                status,
                CreatedByUserId: userId,
                nowUtc),
            CancellationToken.None);

        // Loudly rather than silently. A refusal means the seeded dataset
        // contradicts its own rules — outside the seeded schedule, over
        // capacity, or inside the blackout — and a half-seeded database is
        // harder to diagnose than one that refused to exist.
        if (outcome.Result != BookingCreationResult.Created)
        {
            throw new InvalidOperationException(
                $"Seeding a booking on '{resource.Name}' was refused with {outcome.Result}. "
                + "The seed data no longer satisfies the rules it is seeded through.");
        }

        return bookingId;
    }

    // The seeded schedule opens 09:00–17:00 Monday to Friday, so a booking has
    // to land on a weekday inside those hours; and it has to be in the future,
    // because POST /bookings refuses a wholly elapsed interval
    // (BookingInThePast) and a dataset the API itself would reject is not one
    // anybody can demo from. **Tomorrow onwards**, so the answer never depends
    // on what time of day the seed happened to run.
    //
    // The seeded blackout is skipped as well. dbo.CreateBooking re-checks
    // blackouts under the lock (decision 0023), so a booking on Christmas Day
    // would be refused outright — which would have made the seed throw for two
    // days a year, on the 24th and the 25th, and nowhere else.
    private static DateOnly NextBookableLocalDate(
        IResourceTimeZone zone,
        DateTime nowUtc,
        BlackoutPeriod blackout)
    {
        var date = DateOnly.FromDateTime(zone.ToLocal(nowUtc)).AddDays(1);

        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsBlackedOut(date))
        {
            date = date.AddDays(1);
        }

        return date;

        bool IsBlackedOut(DateOnly candidate) => blackout.Overlaps(
            zone.ToUtcEarliest(candidate.ToDateTime(TimeOnly.MinValue)),
            zone.ToUtcLatest(candidate.AddDays(1).ToDateTime(TimeOnly.MinValue)));
    }

    // ReplaceAvailabilityWindows, which is the only way to set a schedule — the
    // per-window AddAvailabilityWindow was deleted in WP-3 Phase 5 step 4. It
    // carried a live EF trap: a window added to an already-tracked resource is
    // marked Modified rather than Added and saves as a zero-row UPDATE, which
    // surfaces as a 409 for what is plainly an insert (see
    // IResourceRepository.AddAvailabilityWindows). This method was its last
    // caller, and only escaped the trap because it runs against a resource that
    // is itself Added, so the children cascade with it.
    private static void AddWeekdayWindows(Resource resource, Guid actorUserId, DateTime now)
    {
        var weekdays = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
        };

        resource.ReplaceAvailabilityWindows(
            weekdays.Select(weekday => new AvailabilityWindowDefinition(
                Guid.NewGuid(), weekday, new TimeOnly(9, 0), new TimeOnly(17, 0))),
            actorUserId,
            now);
    }
}
