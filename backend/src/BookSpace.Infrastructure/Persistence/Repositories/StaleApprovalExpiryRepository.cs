using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-8 Phase 4 (docs/wp8-plan.md, decisions D3/D3a). The stale-approval
// expiry job's port.
//
// TenantBypassScope's fourth sanctioned caller (alongside
// AuthenticationUserRepository, NotificationRepository and
// NoShowReleaseRepository — see that file's own header): one sweep
// evaluates Bookings across every organisation in a single tick, and there
// is no ICurrentTenant to read a tenant from, because there is no request.
// ApprovalRequests itself needs no bypass — like Notifications, it is not
// one of CLAUDE.md §4.2's six tenant-owned tables, so it carries no query
// filter and no RLS policy at all.
internal sealed class StaleApprovalExpiryRepository : IStaleApprovalExpiryRepository
{
    private readonly BookSpaceDbContext _context;

    public StaleApprovalExpiryRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // IX_ApprovalRequests_Pending narrows on Decision; the ExpiresAtUtc
    // comparison runs on top of that column, which the index itself also
    // carries. Two queries, not N+1: every candidate ApprovalRequest is
    // fetched first, then every matching Booking is fetched in one second
    // query keyed by id — ApprovalRequest has no navigation property to
    // Booking to Include(), the same reasoning
    // BookingRepository.FindApprovableResourceIdsAsync's own header gives
    // for stepping outside EF's normal navigation surface.
    //
    // Both tracked, not AsNoTracking: the job mutates each pair through
    // ApprovalRequest.Expire/Booking.ExpireApproval via TryExpireAsync below.
    public async Task<IReadOnlyList<PendingApprovalExpiry>> FindExpiredCandidatesAsync(
        int batchSize, DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        var approvalRequests = await _context.ApprovalRequests
            .Where(a => a.Decision == ApprovalDecision.Pending
                && a.ExpiresAtUtc != null && a.ExpiresAtUtc <= nowUtc)
            .OrderBy(a => a.ExpiresAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var bookingIds = approvalRequests.Select(a => a.BookingId).ToList();

        // FK_ApprovalRequests_Bookings guarantees a matching row always
        // exists, so the indexer below cannot throw a missing-key exception.
        var bookings = await _context.Bookings
            .IgnoreQueryFilters()
            .Where(b => bookingIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);

        return approvalRequests
            .Select(a => new PendingApprovalExpiry(a, bookings[a.BookingId]))
            .ToList();
    }

    // Isolated per candidate ("isolate failure per item", NotificationDispatchJob's
    // own wording): a concurrent decision on this exact booking between
    // FindExpiredCandidatesAsync's read and this save moved Booking's
    // RowVersion, so SaveChangesAsync throws DbUpdateConcurrencyException
    // here rather than silently overwriting a decision someone just made —
    // whether that decision was a plain-EF Reject (same shape as this write)
    // or an Approve that ran through dbo.ApproveBooking's own lock.
    //
    // **Also inside TenantBypassScope**, covering the write as well as the
    // read — the same fix Phase 3's NoShowReleaseRepository needed: Bookings
    // is one of §4.2's six RLS-protected tables, and RLS's filter predicate
    // governs UPDATE visibility exactly as it governs SELECT. Saving with no
    // tenant session context set would match zero rows at the engine level,
    // indistinguishable from a genuine RowVersion conflict.
    //
    // The tracked entries are detached in every case — success, lost race,
    // or any other failure — so the loop's *next* candidate's
    // SaveChangesAsync does not also try (and fail) to re-save this one's
    // now-stale state: every entity this repository loads shares the same
    // scoped DbContext for the whole run.
    public async Task<bool> TryExpireAsync(
        PendingApprovalExpiry candidate, DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        candidate.Booking.ExpireApproval(nowUtc);
        candidate.ApprovalRequest.Expire(nowUtc);

        var notification = Notification.ForBooking(
            Guid.NewGuid(), candidate.Booking.Id, candidate.Booking.UserId, NotificationKind.ApprovalExpired,
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
            _context.Entry(candidate.Booking).State = EntityState.Detached;
            _context.Entry(candidate.ApprovalRequest).State = EntityState.Detached;
            _context.Entry(notification).State = EntityState.Detached;
        }
    }
}
