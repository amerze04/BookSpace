using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Abstractions;

// The write port for bookings (WP-4 Phase 1b). Declared here, implemented in
// BookSpace.Infrastructure, like every other port — CLAUDE.md §3 keeps EF Core
// out of BookSpace.Application entirely.
//
// **It has no Add(Booking) and never will.** CLAUDE.md §4.1: booking creation
// goes through dbo.CreateBooking, because the capacity guarantee is a
// UPDLOCK/HOLDLOCK range lock that LINQ cannot express and SQL Server has no
// exclusion constraint to fall back on. So the create method here hands the
// procedure its arguments and returns what it decided — the shape of this
// interface is the §4.1 rule made structural, rather than a comment asking
// people to remember it.
//
// **The reads added in Phase 2a are not an exception to that**, and neither is
// the cancel that follows in 2b. §4.1 governs writes that *add* demand against
// Resources.Capacity, because only those can breach it; a read adds none, and a
// cancellation can only ever reduce the units held at an instant, so there is
// nothing for the locking protocol to protect. That is BlackoutCascade's
// argument, which already writes to Bookings through EF for the same reason.
// Stated here so the next reader does not take a projection in this file as
// precedent for a LINQ *insert*, which none of the above permits.
public interface IBookingRepository
{
    // Calls dbo.CreateBooking and reports the outcome. Never throws for a
    // business rejection: a slot being taken is an answer, not a fault, and the
    // handler is what turns the answer into an AppException with a reason code.
    //
    // Runs inside the caller's transaction when there is one (IUnitOfWork), so
    // the ApprovalRequest and Notifications rows written afterwards commit with
    // the booking or not at all. The procedure joins an ambient transaction
    // rather than opening its own.
    Task<BookingCreationOutcome> CreateAsync(NewBooking booking, CancellationToken cancellationToken);

    // ---- The reads (WP-4 Phase 2a, FR-4.4) ----
    //
    // Both take a BookingOwnerFilter the *caller* resolved, rather than reading
    // ICurrentUser here. Decision 0002 puts the "may this actor see another
    // member's booking" question in the Application layer, and BookingReadRules
    // is where it is answered; this port only applies the answer as a predicate.
    // Passing it explicitly is also what makes the widened case auditable — the
    // filter has to be constructed as AnyOwner by name (see BookingOwnerFilter),
    // so a member's list cannot be widened by an omitted argument.
    //
    // Neither read bypasses tenant isolation. Both go through the tenant-filtered
    // DbSet, so §4.2's query filter and RLS are what make another tenant's ids
    // invisible — the owner filter narrows *within* a tenant and never across
    // one (AC-4).

    // One page of GET /bookings, projected. The owner filter is applied as a
    // WHERE clause alongside the query's own from/to/status/resourceId filters.
    Task<PagedResult<ListBookingsQueryResponse>> ListAsync(
        ListBookingsQueryRequest query,
        BookingOwnerFilter owner,
        SortOption? sort,
        CancellationToken cancellationToken);

    // One booking by id, projected, or null if it is not visible to this caller.
    //
    // Null covers all three not-found cases at once — no such id, another
    // tenant's id, another member's booking — which is what lets the handler
    // answer with a single indistinguishable BookingNotFound (AC-4, and
    // BookingNotFoundException for why the third is a 404 rather than a 403).
    // The owner filter is part of the query for that reason: a row the caller
    // may not see is never materialized, so there is no path on which it could
    // reach the wire.
    Task<GetBookingQueryResponse?> FindDetailAsync(
        Guid bookingId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken);

    // ---- The rows derived from a booking (WP-4 Phase 1c) ----
    //
    // These go through EF, not the procedure: they carry no capacity claim and
    // need no lock, and composing notification content in SQL would put it in the
    // one place nothing can unit-test. They commit with the booking because the
    // handler runs all of it inside IUnitOfWork.

    // FR-7.1: a booking on an approval-gated resource enters Pending with a
    // decision record waiting for an approver (WP-5 decides it).
    void AddApprovalRequest(ApprovalRequest approvalRequest);

    // FR-8.1. Rows only — the dispatch job (CLAUDE.md §7) does not exist yet, and
    // UQ_Notifications_Once is what will make the eventual send idempotent
    // (FR-9.4, AC-6). Same arrangement BlackoutCascade already relies on.
    void AddNotifications(IEnumerable<Notification> notifications);

    // Organizations.ApprovalExpiryHours, which drives ApprovalRequest.ExpiresAtUtc
    // and the stale-approval job after it (FR-7.4, FR-9.3). Null means the tenant
    // has set no expiry, and a pending request then waits indefinitely.
    //
    // Takes the org id rather than reading ICurrentTenant, because the caller
    // already has it from the resource — and taking it from the resource is what
    // makes it impossible for this to read a different tenant's setting than the
    // booking is being made against.
    Task<int?> FindApprovalExpiryHoursAsync(Guid orgId, CancellationToken cancellationToken);

    // Separate from the mutations, as on the other repositories: the handler owns
    // the unit of work, so one save covers everything it staged.
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

// Everything dbo.CreateBooking needs, and nothing it does not.
//
// **Id is chosen by the caller**, before the unit of work starts, which is not
// a style preference: IUnitOfWork's delegate can be re-run after a deadlock
// (1205), and a procedure that minted its own id would insert a second booking
// on the retry instead of the same one.
//
// **NowUtc is passed in rather than read as SYSUTCDATETIME()** inside the
// procedure. CLAUDE.md §4.3: IClock.UtcNow is truncated to whole seconds
// because datetime2(0) *rounds* on write, so a stamp taken in SQL could round
// to a different second than the one the response is built from, and a client
// reading the booking back would see a timestamp it was never given.
public sealed record NewBooking(
    Guid Id,
    Guid ResourceId,
    Guid UserId,
    Guid? RecurrenceRuleId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title,
    BookingStatus Status,
    Guid CreatedByUserId,
    DateTime NowUtc);

// What the procedure decided, and how much room was left when it decided it.
//
// RemainingCapacity is the units free across the requested interval *before*
// this booking: the figure the two capacity refusals are split on, and on the
// Created path what is left after it. Null where the question did not arise —
// the resource was missing, archived, or blacked out.
public sealed record BookingCreationOutcome(BookingCreationResult Result, int? RemainingCapacity);

// The procedure's result codes. Deliberately not ReasonCodes strings: those are
// the Application layer's wire contract and the mapping to them is the
// handler's, so a change to what a client sees never reaches into SQL.
public enum BookingCreationResult
{
    Created,

    // The resource is not visible on this connection. Also what a missing tenant
    // session context produces, and that is the point — see BookingRepository
    // for why the procedure reads Resources before it counts anything.
    ResourceNotFound,

    // FR-3.5. Checked again here even though the handler checks it first,
    // because the procedure is the last gate before a row exists.
    ResourceArchived,

    // Decision 0001 gives a blackout absolute priority, so the check is repeated
    // under the lock — see the procedure for the race that makes this necessary
    // rather than merely tidy.
    BlackoutPeriod,

    // No units free at some instant inside the interval.
    SlotUnavailable,

    // Units free throughout, but fewer than were asked for.
    CapacityExceeded,
}
