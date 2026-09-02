using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// FR-3.4. Declared here, implemented in BookSpace.Infrastructure, for the same
// reason as IResourceRepository: CLAUDE.md §3 keeps EF Core out of
// BookSpace.Application entirely.
//
// **One port for three tables**, which is the design decision worth defending.
// Creating a blackout is not an insert: decision 0001 gives a blackout absolute
// priority, so it also cancels every booking it overlaps and enqueues a
// Notifications row per cancellation. Those writes have to land or fail
// together, so they have to share a unit of work — and the way this codebase
// expresses a unit of work is a repository that owns SaveChangesAsync. Splitting
// them across a blackout port and a booking port would leave correctness
// resting on the unstated fact that both resolve the same request-scoped
// DbContext, which is true and much too easy to break.
//
// One SaveChangesAsync covers all of it. No explicit transaction is needed and
// none is used: SaveChanges is already transactional, so CLAUDE.md §5's
// "wrap the unit of work in CreateExecutionStrategy().ExecuteAsync(...)" does
// not apply — that rule exists for callers who would otherwise reach for
// BeginTransaction, and this is not one.
//
// Nothing here bypasses tenant isolation. Every query goes through the
// tenant-filtered DbSet, so decision 0014's coverage of BlackoutPeriods and
// §4.2's coverage of Bookings and Resources are what make another tenant's ids
// invisible — not a predicate written in this file that someone could forget.
public interface IBlackoutPeriodRepository
{
    // The resource the blackout will hang off. Null means "no such resource in
    // this tenant", which after §4.2's filters is also the answer for another
    // tenant's real id (AC-4).
    //
    // Read through this port rather than IResourceRepository.FindForUpdateAsync
    // on purpose: nothing here edits the resource, so "for update" would
    // misdescribe it, and that loader Includes the availability windows this
    // handler has no use for. Keeping it here also keeps the whole unit of work
    // behind one port — see the header.
    Task<Resource?> FindOwningResourceAsync(Guid resourceId, CancellationToken cancellationToken);

    // The bookings decision 0001's cascade must cancel: those on this resource
    // that overlap [startsAtUtc, endsAtUtc) and are still cancellable.
    //
    // The "still cancellable" half is Booking.CanBeCancelledForBlackout's rule —
    // Pending or Confirmed, and not already finished — restated as a SQL
    // predicate. The domain method is the authority and re-checks it; this filter
    // exists so the handler does not load every booking the resource has ever
    // had in order to skip most of them. If the two ever disagree, the domain
    // guard throws rather than letting the wrong row through.
    //
    // Tracked, not AsNoTracking: the caller mutates each one through
    // CancelForBlackout and the change has to be saved.
    Task<IReadOnlyList<Booking>> FindBookingsToCancelAsync(
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    // The tracked blackout, for an edit or a delete. Scoped to the resource as
    // well as to the tenant, so a real blackout id belonging to a different
    // resource returns null rather than being edited through the wrong route.
    //
    // Returns the entity rather than a DTO because the caller mutates it through
    // Revise — the same reason IResourceRepository.FindForUpdateAsync returns a
    // Resource.
    Task<BlackoutPeriod?> FindForUpdateAsync(
        Guid resourceId,
        Guid blackoutPeriodId,
        CancellationToken cancellationToken);

    void Add(BlackoutPeriod blackoutPeriod);

    // A hard delete, and the only one in this codebase. CLAUDE.md §4.5 is about
    // users and resources, whose history has to stay readable; a blackout carries
    // none — the reason a booking was cancelled is a text snapshot on the booking
    // itself, not a foreign key. See DeleteBlackoutPeriodCommandRequest.
    void Remove(BlackoutPeriod blackoutPeriod);

    // The cascade's notifications (FR-8.1). Stated as inserts explicitly, like
    // IResourceRepository.AddAvailabilityWindows — a Notification is minted with
    // an id the caller chose, and EF marks a keyed entity it discovers through a
    // navigation as Modified rather than Added. These arrive as a plain list
    // rather than through a navigation, so AddRange is also simply the clearest
    // statement of intent.
    void AddNotifications(IEnumerable<Notification> notifications);

    Task<PagedResult<ListBlackoutPeriodsQueryResponse>> ListAsync(
        ListBlackoutPeriodsQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken);

    // Separate from the mutations, matching IResourceRepository: the handler owns
    // the unit of work, so one save covers the blackout, the cancellations and
    // the notifications together.
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
