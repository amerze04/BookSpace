using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// The read port behind GET /resources/{id}/availability (WP-3 Phase 5). Declared
// here, implemented in BookSpace.Infrastructure, for the same reason as the other
// repositories: CLAUDE.md §3 keeps EF Core out of BookSpace.Application entirely.
//
// **Its own port over three tables**, rather than methods bolted onto
// IResourceRepository, and the reasoning follows IBlackoutPeriodRepository's: one
// question needs the schedule, the blackouts and the bookings together, and a
// port whose shape matches the question is easier to defend than three unrelated
// members spread across two existing ports. It differs from that one in owning
// no writes at all — there is no SaveChangesAsync here, because nothing this
// endpoint does changes anything.
//
// The two interval methods return **Domain value types** rather than DTOs or
// entities, which is deliberate and unlike IResourceRepository's projections.
// The consumer is not a response mapper, it is AvailabilityCalculator — so the
// port speaks the calculator's language, and the handler is left with nothing to
// convert. It also means neither method can hand back a row's other columns by
// accident.
//
// Nothing here bypasses tenant isolation. All three queries go through the
// tenant-filtered DbSets, so §4.2 and decision 0014 are what make another
// tenant's ids invisible — not a predicate in the implementation that someone
// could forget.
public interface IAvailabilityRepository
{
    // The resource and its weekly schedule. Null means "no such resource in this
    // tenant", which after §4.2's filters is also the answer for another tenant's
    // real id (AC-4).
    //
    // Returns the aggregate rather than a projection because the calculator reads
    // it directly — Capacity, MinDurationMinutes and the windows. AsNoTracking on
    // the implementation side: this is a read, and nothing is mutated.
    Task<Resource?> FindWithScheduleAsync(Guid resourceId, CancellationToken cancellationToken);

    // The blackouts overlapping the queried span, as bare intervals — the reason
    // for one is a string this endpoint has no use for, and FR-3.4's override is
    // about time, not about why.
    //
    // Overlap, not containment: a blackout that started last week and runs
    // through the queried range blocks it just as much as one that begins inside
    // it. Overlapping blackouts are allowed (decision 0019) and need no special
    // handling — IntervalAlgebra.Subtract merges its inputs.
    Task<IReadOnlyList<UtcInterval>> FindBlackoutIntervalsAsync(
        Guid resourceId,
        UtcInterval span,
        CancellationToken cancellationToken);

    // The bookings holding units in the queried span: Pending and Confirmed
    // only, which is the same live set decision 0005's capacity arithmetic uses
    // everywhere else (and the same one FindBookingsToCancelAsync filters to).
    // Cancelled, Rejected, NoShow and Completed hold nothing.
    //
    // Deliberately **not** filtered to the future. A range may legitimately end
    // in the past — a member looking back at last week — and availability then
    // was consumed by the bookings that existed then. Overlap again, so a booking
    // that began before the span still holds its units inside it.
    Task<IReadOnlyList<BookedQuantity>> FindBookedQuantitiesAsync(
        Guid resourceId,
        UtcInterval span,
        CancellationToken cancellationToken);
}
