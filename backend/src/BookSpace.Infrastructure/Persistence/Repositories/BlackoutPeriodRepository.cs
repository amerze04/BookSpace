using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.BlackoutPeriods;
using BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// FR-3.4, plus decision 0001's cascade. No IgnoreQueryFilters and no
// TenantBypassScope anywhere in this file, deliberately: every query goes
// through the tenant-filtered DbSet, so CLAUDE.md §4.2's query filter and RLS
// are what make another tenant's rows invisible rather than a WHERE clause
// written here that someone could forget. Decision 0014 is what makes that true
// of BlackoutPeriods as well as Resources and Bookings.
internal sealed class BlackoutPeriodRepository : IBlackoutPeriodRepository
{
    private readonly BookSpaceDbContext _context;

    public BlackoutPeriodRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // FirstOrDefaultAsync, never Find(): Find can answer from the change tracker
    // without querying, and a tracked entity skips the query filter
    // (CLAUDE.md §4.2).
    //
    // No AsNoTracking, and it matters. The blackout's composite FK is
    // (OrgId, ResourceId), so EF has to be able to see that the principal row
    // exists in this unit of work; tracking it also means a concurrent archive
    // of the same resource is seen by this context rather than only by the
    // database.
    public Task<Resource?> FindOwningResourceAsync(Guid resourceId, CancellationToken cancellationToken) =>
        _context.Resources.FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);

    // Overlap is half-open on both sides — b.StartsAtUtc < endsAtUtc &&
    // startsAtUtc < b.EndsAtUtc — which is BlackoutPeriod.Overlaps' predicate and
    // the same one dbo.CreateBooking will use in WP-4. Adjacency is therefore not
    // overlap: a booking ending exactly when the blackout starts survives, which
    // is right, because the room is still usable up to that instant.
    //
    // The status and EndsAtUtc tests mirror Booking.CanBeCancelledForBlackout —
    // see IBlackoutPeriodRepository for why the domain method is the authority
    // and this is only the filter that avoids loading the rest.
    //
    // Ordered so the response lists cancellations in a stable, readable order
    // rather than whatever order the engine returns.
    public async Task<IReadOnlyList<Booking>> FindBookingsToCancelAsync(
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await _context.Bookings
            .Where(b => b.ResourceId == resourceId
                && b.StartsAtUtc < endsAtUtc
                && startsAtUtc < b.EndsAtUtc
                && b.EndsAtUtc > nowUtc
                && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .OrderBy(b => b.StartsAtUtc)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);

    // Both ids in the predicate, so the route has to name the blackout's real
    // owner. FirstOrDefaultAsync, never Find(), for the CLAUDE.md §4.2 reason:
    // Find can answer from the change tracker without querying, and a tracked
    // entity skips the query filter.
    //
    // No AsNoTracking: the caller mutates this through Revise, or removes it.
    public Task<BlackoutPeriod?> FindForUpdateAsync(
        Guid resourceId,
        Guid blackoutPeriodId,
        CancellationToken cancellationToken) =>
        _context.BlackoutPeriods.FirstOrDefaultAsync(
            b => b.Id == blackoutPeriodId && b.ResourceId == resourceId, cancellationToken);

    public void Add(BlackoutPeriod blackoutPeriod) => _context.BlackoutPeriods.Add(blackoutPeriod);

    public void Remove(BlackoutPeriod blackoutPeriod) => _context.BlackoutPeriods.Remove(blackoutPeriod);

    public void AddNotifications(IEnumerable<Notification> notifications) =>
        _context.AddRange(notifications);

    public Task<PagedResult<ListBlackoutPeriodsQueryResponse>> ListAsync(
        ListBlackoutPeriodsQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken)
    {
        var blackouts = _context.BlackoutPeriods.Where(b => b.ResourceId == query.ResourceId);

        // Overlap, not containment: a blackout that started before the window and
        // runs into it is exactly what a client asking "what blocks next week"
        // needs to see. Each bound is applied independently so either can be
        // omitted.
        if (query.From is { } from)
        {
            blackouts = blackouts.Where(b => b.EndsAtUtc > from);
        }

        if (query.To is { } to)
        {
            blackouts = blackouts.Where(b => b.StartsAtUtc < to);
        }

        // Projection after ordering, so ToPagedResultAsync still sees the OrderBy
        // in the expression tree and COUNT(*) runs over the filtered set rather
        // than a materialized list.
        return ApplyOrder(blackouts, sort)
            .Select(b => new ListBlackoutPeriodsQueryResponse(
                b.Id,
                b.ResourceId,
                b.StartsAtUtc,
                b.EndsAtUtc,
                b.Reason,
                b.CreatedAtUtc))
            .ToPagedResultAsync(query, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);

    // Maps a canonical whitelist name (BlackoutPeriodSortFields) onto a typed
    // OrderBy, the Infrastructure half of the split described on
    // IResourceRepository: the sort string never enters the expression.
    //
    // Every branch ends in ThenBy(Id). Offset paging over a non-unique order is
    // undefined among tied rows, and two blackouts starting at the same instant
    // is ordinary rather than exotic — a resource blacked out for two separate
    // reasons over the same holiday. ToPagedResultAsync can only check that *an*
    // ordering exists; the unique tiebreak is this method's job
    // (docs/decisions/0015).
    //
    // Chronological by default. A blackout list is read as a schedule, and the
    // question is almost always "when next", so ascending start is the order that
    // needs no explanation.
    private static IQueryable<BlackoutPeriod> ApplyOrder(
        IQueryable<BlackoutPeriod> blackouts,
        SortOption? sort) =>
        sort switch
        {
            { Field: BlackoutPeriodSortFields.StartsAtUtc, Descending: true } =>
                blackouts.OrderByDescending(b => b.StartsAtUtc).ThenBy(b => b.Id),
            { Field: BlackoutPeriodSortFields.StartsAtUtc } =>
                blackouts.OrderBy(b => b.StartsAtUtc).ThenBy(b => b.Id),
            { Field: BlackoutPeriodSortFields.EndsAtUtc, Descending: true } =>
                blackouts.OrderByDescending(b => b.EndsAtUtc).ThenBy(b => b.Id),
            { Field: BlackoutPeriodSortFields.EndsAtUtc } =>
                blackouts.OrderBy(b => b.EndsAtUtc).ThenBy(b => b.Id),
            _ => blackouts.OrderBy(b => b.StartsAtUtc).ThenBy(b => b.Id),
        };
}
