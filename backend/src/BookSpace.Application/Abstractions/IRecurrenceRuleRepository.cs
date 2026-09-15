using BookSpace.Application.Features.Bookings;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// The write port for recurrence rules (WP-5 Phase 1b). Declared here,
// implemented in BookSpace.Infrastructure, like every other port.
//
// Unlike IBookingRepository, this one embodies no §4.1 constraint: a
// RecurrenceRule carries no capacity claim of its own — it is
// Bookings.RecurrenceRuleId that an occurrence's row points back at, and each
// occurrence's capacity guarantee still goes through dbo.CreateBooking exactly
// as a one-off booking's does. So this is a plain EF add, on the same footing
// as AddApprovalRequest / AddNotifications on IBookingRepository.
public interface IRecurrenceRuleRepository
{
    void Add(RecurrenceRule rule);

    // ---- Idempotent creation (hardening pass, item 11) ---------------------

    // Tracked: FindOperationAsync's caller mutates the row it returns
    // (MarkActive/MarkFailed/RestartWith) and saves it in the same unit of
    // work as everything else the handler does up front.
    Task<RecurrenceCreationOperation?> FindOperationAsync(
        Guid orgId, Guid userId, string idempotencyKey, CancellationToken cancellationToken);

    void AddOperation(RecurrenceCreationOperation operation);

    // Bug fix, item 11's own found gap: the detach half of AddOperation, for
    // an Added-but-never-saved operation this same request loses a race over
    // (see TrySaveNewOperationAsync). Same shape as Remove(RecurrenceRule).
    void RemoveOperation(RecurrenceCreationOperation operation);

    // The tracked rule a resume/replay reuses instead of minting a new one.
    // Tenant-filtered like every other read here, though in practice the
    // caller already knows this id came from a same-tenant operation row.
    Task<RecurrenceRule?> FindByIdAsync(Guid recurrenceRuleId, CancellationToken cancellationToken);

    // Bug fix, item 11's own found gap: SaveChangesAsync's own twin for the
    // one call site where a failure is an expected outcome rather than a
    // fault — two literally-simultaneous first-time requests for the same
    // idempotency key, racing to insert the same (OrgId, UserId,
    // IdempotencyKey) row. True on an ordinary successful save; false only
    // when this attempt lost that race to UQ_RecurrenceCreationOperations_Org_User_Key,
    // in which case the caller detaches what it staged (Remove /
    // RemoveOperation) and resumes the winner's row instead, exactly as an
    // explicit retry against an existing operation already does.
    Task<bool> TrySaveNewOperationAsync(CancellationToken cancellationToken);

    // ---- The cancel (WP-5 Phase 2, FR-5.3, decision 0002 reapplied) ----

    // The tracked series, for cancellation — same shape as
    // IBookingRepository.FindForCancellationAsync one level up. BookingOwnerFilter
    // is reused verbatim rather than a parallel RecurrenceRuleOwnerFilter type:
    // decision 0002's reach ("owner, or a TenantAdmin in the same tenant") is
    // identical regardless of which entity is being reached, so the filter
    // itself does not need to know which one it is.
    //
    // Null covers all three not-found cases at once — no such id, another
    // tenant's id (the query filter, decision 0025), another member's series —
    // so the handler answers with one indistinguishable RecurrenceRuleNotFound
    // (AC-4). The owner filter is part of the query, not a check after loading,
    // for the same reason it is on the booking cancel: a series the caller may
    // not reach is never loaded, so there is no state to accidentally leak.
    Task<RecurrenceRule?> FindForCancellationAsync(
        Guid recurrenceRuleId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken);

    // The compensating half of Add, for a series that reserved nothing
    // (WP-5 Phase 1b). The rule is persisted before any occurrence is
    // attempted — Bookings.RecurrenceRuleId is a real FK — so if every
    // occurrence ends up skipped or refused, this is what stops the 422
    // response from leaving an orphaned, Active series behind it with
    // nothing booked against it. Safe to call because nothing else
    // references the row at that point: no Booking was created (that is
    // the condition for calling this at all), and the handler defers
    // persisting any skipped-occurrence Notifications until it knows at
    // least one occurrence succeeded, so there is nothing else to clean up.
    void Remove(RecurrenceRule rule);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
