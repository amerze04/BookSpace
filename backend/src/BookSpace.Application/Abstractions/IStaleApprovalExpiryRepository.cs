using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// WP-8 Phase 4 (docs/wp8-plan.md, FR-9.3, decisions D3/D3a). The
// stale-approval expiry job's port: sweep every Pending ApprovalRequest
// whose ExpiresAtUtc has elapsed, expire it and reject its booking, and
// queue the notification telling the owner why.
//
// A repository of its own, matching INoShowReleaseRepository's shape and for
// the identical reason: one sweep tick evaluates ApprovalRequests across
// every organisation at once, which IBookingRepository's own header frames
// itself around never doing.
public interface IStaleApprovalExpiryRepository
{
    // Every still-Pending ApprovalRequest whose ExpiresAtUtc has elapsed,
    // paired with the Booking it gates — ApprovalRequest carries no
    // navigation property to Booking (only the FK, BookingId), so the pair
    // is loaded together here rather than the job doing a second lookup per
    // row. Both tracked, since the job mutates each through
    // ApprovalRequest.Expire/Booking.ExpireApproval via TryExpireAsync
    // below. Bounded and ordered by ExpiresAtUtc, mirroring
    // FindNoShowCandidatesAsync's own reasoning.
    Task<IReadOnlyList<PendingApprovalExpiry>> FindExpiredCandidatesAsync(
        int batchSize, DateTime nowUtc, CancellationToken cancellationToken);

    // Expires one ApprovalRequest and rejects its Booking in a single save,
    // isolated from every other candidate in the batch. False means a
    // concurrent decision — a human approving or rejecting, or
    // dbo.ApproveBooking's own re-check — landed on this exact booking
    // between the read above and this call. Booking's own RowVersion is
    // what catches it: ApprovalRequests carries no concurrency token of its
    // own, but both writes share one SaveChangesAsync, so a Booking-level
    // conflict rolls the whole attempt back with it. Not a failure — the
    // same reasoning INoShowReleaseRepository.TryReleaseAsync gives for its
    // own false.
    Task<bool> TryExpireAsync(
        PendingApprovalExpiry candidate, DateTime nowUtc, CancellationToken cancellationToken);
}

// A Pending ApprovalRequest paired with the Booking it gates, both tracked
// by the same DbContext — see FindExpiredCandidatesAsync's own header for
// why the pair travels together rather than the job re-deriving one from
// the other.
public sealed record PendingApprovalExpiry(ApprovalRequest ApprovalRequest, Booking Booking);
