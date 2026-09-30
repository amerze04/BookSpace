using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-8 Phase 3 (docs/wp8-plan.md, decision 0004). The no-show release job's
// port.
//
// TenantBypassScope's third sanctioned caller (alongside
// AuthenticationUserRepository and NotificationRepository — see that file's
// own header): one sweep evaluates Confirmed bookings across every
// organisation in a single tick, and there is no ICurrentTenant to read a
// tenant from, because there is no request.
internal sealed class NoShowReleaseRepository : INoShowReleaseRepository
{
    private readonly BookSpaceDbContext _context;

    public NoShowReleaseRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // IX_Bookings_NoShowSweep narrows on Status/CheckedInAtUtc; the grace
    // comparison against each row's own organisation is evaluated on top of
    // that, exactly as CLAUDE.md §7 and decision 0004 describe. The
    // correlated subquery for NoShowGraceMinutes mirrors
    // BookingRepository.ListAsync's own resource-name lookup: Booking
    // carries no navigation property to Organization (only the
    // denormalized OrgId, decision 0006), and the FK guarantees a row
    // always exists, so First() cannot throw.
    //
    // Tracked, not AsNoTracking: the job mutates each one through
    // Booking.MarkNoShow via TryReleaseAsync below.
    public async Task<IReadOnlyList<Booking>> FindNoShowCandidatesAsync(
        int batchSize, DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        return await _context.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.Status == BookingStatus.Confirmed && b.CheckedInAtUtc == null)
            .Where(b => b.StartsAtUtc.AddMinutes(
                _context.Organizations.Where(o => o.Id == b.OrgId)
                    .Select(o => o.NoShowGraceMinutes)
                    .First()) < nowUtc)
            .OrderBy(b => b.StartsAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    // Isolated per booking ("isolate failure per item", NotificationDispatchJob's
    // own wording): a concurrent check-in on this booking between
    // FindNoShowCandidatesAsync's read and this save moved its RowVersion, so
    // SaveChangesAsync throws DbUpdateConcurrencyException here rather than
    // silently overwriting someone who just arrived.
    //
    // **Also inside TenantBypassScope**, and this half is load-bearing rather
    // than copied out of caution: unlike Notifications (no RLS policy at
    // all — see RecordOutcomeAsync's own comment), Bookings is one of §4.2's
    // six RLS-protected tables, and RLS's filter predicate governs UPDATE
    // visibility exactly as it governs SELECT. Saving this booking with no
    // tenant session context set would match zero rows at the engine level —
    // indistinguishable from a genuine RowVersion conflict, and silently
    // wrong for the wrong reason.
    //
    // The tracked entries are detached in every case — success, lost race, or
    // any other failure — so the loop's *next* booking's SaveChangesAsync does
    // not also try (and fail) to re-save this one's now-stale state: every
    // booking this repository loads shares the same scoped DbContext for the
    // whole run.
    public async Task<bool> TryReleaseAsync(Booking booking, DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        booking.MarkNoShow(nowUtc);

        var notification = Notification.ForBooking(
            Guid.NewGuid(), booking.Id, booking.UserId, NotificationKind.NoShowReleased,
            nowUtc, createdByUserId: null, nowUtc);
        _context.Notifications.Add(notification);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        finally
        {
            _context.Entry(booking).State = EntityState.Detached;
            _context.Entry(notification).State = EntityState.Detached;
        }
    }
}
