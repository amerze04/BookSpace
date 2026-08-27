using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// WP-1 seed data: a realistic multi-tenant dataset. Deliberately stops short
// of Bookings (and anything anchored to one — ApprovalRequests,
// Notifications): CLAUDE.md §4.1 requires all Booking writes to go through
// dbo.CreateBooking, which doesn't exist yet, so there is nothing legitimate
// to seed there. RefreshTokens are skipped too — they're issued at login,
// not meaningful as static seed data.
public static class SeedData
{
    // Development only — see the comment at its use site.
    public const string SeedPassword = "Passw0rd!";

    public static async Task SeedAsync(BookSpaceDbContext context, IPasswordHasher passwordHasher)
    {
        if (await context.Organizations.AnyAsync())
            return;

        var now = DateTime.UtcNow;

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

        SeedTenant(context, acme, "acme.test", now, seedPasswordHash);
        SeedTenant(context, globex, "globex.test", now, seedPasswordHash);

        await context.SaveChangesAsync();
    }

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

    private static void SeedTenant(
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

        var openResource = new Resource(
            Guid.NewGuid(), org.Id, "Conference Room A", "Room",
            capacity: 8, timeZoneId: org.TimeZoneId, requiresApproval: false,
            minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main conference room", createdByUserId: tenantAdmin.Id, nowUtc: now);
        AddWeekdayWindows(openResource, tenantAdmin.Id, now);
        context.Resources.Add(openResource);

        var approvedResource = new Resource(
            Guid.NewGuid(), org.Id, "3D Printer", "Equipment",
            capacity: 1, timeZoneId: org.TimeZoneId, requiresApproval: true,
            minDurationMinutes: 60, maxDurationMinutes: 180,
            description: "Shared prototyping printer", createdByUserId: tenantAdmin.Id, nowUtc: now);
        approvedResource.AddApprover(approver.Id, tenantAdmin.Id, now);
        AddWeekdayWindows(approvedResource, tenantAdmin.Id, now);
        context.Resources.Add(approvedResource);

        var blackout = new BlackoutPeriod(
            Guid.NewGuid(), openResource.Id,
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
            Guid.NewGuid(), openResource.Id, memberOne.Id,
            RecurrenceFrequency.Weekly, intervalValue: 1,
            localStartTime: new TimeOnly(9, 0), localEndTime: new TimeOnly(9, 30),
            startDate: standupStart, endDate: standupStart.AddYears(2), occurrenceCount: null,
            timeZoneId: org.TimeZoneId, createdByUserId: memberOne.Id, nowUtc: now);
        context.RecurrenceRules.Add(recurrenceRule);
    }

    private static void AddWeekdayWindows(Resource resource, Guid actorUserId, DateTime now)
    {
        var weekdays = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
        };

        foreach (var weekday in weekdays)
        {
            resource.AddAvailabilityWindow(
                new AvailabilityWindow(Guid.NewGuid(), resource.Id, weekday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
                actorUserId, now);
        }
    }
}
