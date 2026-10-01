using BookSpace.Domain.Enums;

namespace BookSpace.Application.Abstractions;

// WP-8 Phase 2 (docs/wp8-plan.md). The dispatch job's own port — distinct from
// IBookingRepository, which is written entirely around one tenant's own
// request (BookingOwnerFilter, ApprovalReach). A job has no request and no
// tenant: one run claims due rows across every organisation at once, so
// nothing here takes a tenant-scoped filter at all.
public sealed record ClaimedNotification(
    Guid Id,
    Guid? BookingId,
    Guid? RecurrenceRuleId,
    DateOnly? OccurrenceDate,
    Guid RecipientUserId,
    NotificationKind Kind,
    int Attempts);

public interface INotificationRepository
{
    // Claims up to batchSize due, unsent rows in one atomic
    // UPDLOCK/READPAST statement (CLAUDE.md §7) that also increments each
    // claimed row's Attempts — claiming *is* the attempt, so Attempts counts
    // how many times a worker has picked a row up to try it, not how many
    // outcomes were later recorded (see Notification.MarkOutcome).
    //
    // A row already at maxAttempts is never claimed again — the retry cap —
    // and one still under it is skipped until its own exponential backoff
    // window (backoffBase * 2^Attempts, measured from its last claim) has
    // elapsed, so a transiently-failing item is retried with growing spacing
    // rather than on every tick.
    //
    // Hardening pass, finding 6: candidates are ordered (SendAtUtc,
    // CreatedAtUtc, Id) *inside the same atomic statement* — SQL Server's
    // own `UPDATE TOP (n)` promises no order at all, which meant the oldest
    // overdue row had no guarantee of ever being claimed ahead of a newer
    // one under a sustained backlog. Selecting and updating remain one
    // statement (a CTE, not two round trips), so the ordering costs nothing
    // in atomicity.
    //
    // Hardening pass, finding 2: jobName/ownerId fence the claim against
    // *current* lease ownership, evaluated against dbo.JobLeases' own row —
    // a worker whose lease has already lapsed (even if it has not noticed
    // yet) claims nothing, closing the one place this job's own lease
    // exclusivity actually matters: a claim gates an external side effect
    // (an email send) that nothing can undo once started, unlike the
    // in-database mutations NoShowReleaseJob/StaleApprovalExpiryJob perform,
    // which Bookings.RowVersion already protects on its own (see those two
    // repositories' own headers for why they do not need this).
    Task<IReadOnlyList<ClaimedNotification>> ClaimDueAsync(
        int batchSize,
        int maxAttempts,
        TimeSpan backoffBase,
        DateTime nowUtc,
        string jobName,
        Guid ownerId,
        CancellationToken cancellationToken);

    // Composes the email for a claimed row, resolving whichever anchor it
    // carries (a Booking, or a RecurrenceRule with or without an
    // OccurrenceDate — decisions 0008/0026) and its recipient, none of which
    // belong to a known tenant at this point — see NotificationRepository for
    // where TenantBypassScope is entered.
    //
    // Null means this notification's own subject is stale and no longer
    // needs an email — the booking or series it describes moved on before
    // dispatch got to it. Hardening pass, finding 5, widens this beyond its
    // original single case (a Reminder whose Booking is no longer Confirmed,
    // decision D10): see ComposeForBooking's own header for the full list.
    // The caller still records the attempt as handled, never retried.
    //
    // nowUtc is the run's own instant (hardening pass, finding 5) — needed
    // for the one staleness check that is about *time* rather than *status*:
    // a Reminder whose booking's own interval has already ended.
    Task<EmailMessage?> BuildEmailAsync(
        ClaimedNotification notification, DateTime nowUtc, CancellationToken cancellationToken);

    // Ordinary EF: Notifications carries no tenant filter or RLS policy at
    // all (it is not one of §4.2's six tables), so this is a plain read of
    // the tracked entity and Notification.MarkOutcome, exactly the pattern
    // every other write in this codebase follows.
    Task RecordOutcomeAsync(
        Guid notificationId, DateTime nowUtc, bool succeeded, string? error, CancellationToken cancellationToken);
}
