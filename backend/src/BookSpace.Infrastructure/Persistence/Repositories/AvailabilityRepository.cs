using BookSpace.Application.Abstractions;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// IAvailabilityRepository against EF Core. Three reads, no writes.
//
// Every query goes through the tenant-filtered DbSets, so another tenant's
// resource, blackouts and bookings are invisible here without this file saying
// anything about OrgId — §4.2 for Resources and Bookings, decision 0014 for
// BlackoutPeriods. Nothing calls IgnoreQueryFilters.
internal sealed class AvailabilityRepository : IAvailabilityRepository
{
    private readonly BookSpaceDbContext _context;

    public AvailabilityRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // FirstOrDefaultAsync, never DbSet.Find(): Find can answer from the change
    // tracker without querying at all, which skips the query filter
    // (CLAUDE.md §4.2).
    public Task<Resource?> FindWithScheduleAsync(Guid resourceId, CancellationToken cancellationToken) =>
        _context.Resources
            .AsNoTracking()
            .Include(r => r.AvailabilityWindows)
            .FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);

    public async Task<IReadOnlyList<UtcInterval>> FindBlackoutIntervalsAsync(
        Guid resourceId,
        UtcInterval span,
        CancellationToken cancellationToken)
    {
        // Pulled into locals before the predicate. EF would parameterise the
        // member access too, but a plain local is what the rest of this codebase's
        // overlap predicates look like (FindBookingsToCancelAsync), and the
        // half-open comparison is easier to check when both sides are simple.
        var fromUtc = span.StartUtc;
        var toUtc = span.EndUtc;

        var rows = await _context.BlackoutPeriods
            .AsNoTracking()
            .Where(b => b.ResourceId == resourceId
                && b.StartsAtUtc < toUtc
                && fromUtc < b.EndsAtUtc)
            .Select(b => new { b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(cancellationToken);

        return Intervals(rows.Select(r => (r.StartsAtUtc, r.EndsAtUtc)));
    }

    public async Task<IReadOnlyList<BookedQuantity>> FindBookedQuantitiesAsync(
        Guid resourceId,
        UtcInterval span,
        CancellationToken cancellationToken)
    {
        var fromUtc = span.StartUtc;
        var toUtc = span.EndUtc;

        // Pending and Confirmed only — the live set decision 0005's capacity
        // arithmetic uses everywhere. The enum is compared as an enum, not a
        // string: the column stores names (CLAUDE.md §5) and EF's value converter
        // handles the translation.
        var rows = await _context.Bookings
            .AsNoTracking()
            .Where(b => b.ResourceId == resourceId
                && b.StartsAtUtc < toUtc
                && fromUtc < b.EndsAtUtc
                && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .Select(b => new { b.StartsAtUtc, b.EndsAtUtc, b.Quantity })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new BookedQuantity(new UtcInterval(r.StartsAtUtc, r.EndsAtUtc), r.Quantity))
            .ToList();
    }

    // Projected to an anonymous type first and constructed here, rather than
    // `Select(b => new UtcInterval(...))` in the query. Two reasons, and the
    // second is the real one: it keeps the translation obvious, and UtcInterval's
    // constructor insists on DateTimeKind.Utc — which arrives only because
    // OnModelCreating applies the §4.3 Kind converter to every DateTime property.
    // Constructing after materialisation means that convention is exercised
    // exactly as the rest of the application exercises it, so if it ever stops
    // being applied these queries fail loudly instead of returning instants a
    // browser would read as local time.
    private static IReadOnlyList<UtcInterval> Intervals(
        IEnumerable<(DateTime StartsAtUtc, DateTime EndsAtUtc)> rows) =>
        rows.Select(r => new UtcInterval(r.StartsAtUtc, r.EndsAtUtc)).ToList();
}
