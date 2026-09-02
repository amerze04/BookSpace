using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.BlackoutPeriods;

// Decision 0001's cascade: a blackout has absolute priority, so every booking it
// overlaps is cancelled and the booking's owner is notified.
//
// An Application-layer orchestration because it spans two aggregates, exactly as
// 0001 predicted ("not something a single Booking or BlackoutPeriod entity
// method can do alone"). Static, with no injected dependencies, matching the
// Rules classes: it is a pure function of a blackout, the bookings it hit, and
// the clock. Extracted rather than inlined into the create handler because
// editing a blackout to a wider range has to run the identical cascade (0001
// says so), and that endpoint arrives in the next step.
//
// **On CLAUDE.md §4.1**, which this is the first code in the repository to sit
// near: booking *creation and approval* must go through dbo.CreateBooking /
// dbo.ApproveBooking, and this is neither. The rule exists because those paths
// add demand against Resources.Capacity and need UPDLOCK/HOLDLOCK range locks
// to compare the sum of overlapping Quantity against it. A cancellation can only
// ever *reduce* the units held at an instant, so it cannot produce an
// overbooking — there is nothing for the locking protocol to protect. The
// lost-update case is covered independently: Bookings.RowVersion is a
// concurrency token, so two admins blacking out overlapping ranges at once means
// one of them gets DbUpdateConcurrencyException, already mapped to 409.
//
// That reasoning is written here rather than left to be re-derived because the
// next reader will otherwise cite this file as precedent for a LINQ *insert*
// into Bookings, which §4.1 exists to prevent and which none of the above
// permits. Confirmed with the repo owner, 2026-09-02.
internal static class BlackoutCascade
{
    // Reason text stored on each cancelled booking. A snapshot, not a foreign
    // key: CancellationReason is NVARCHAR(300), so it keeps saying why the
    // booking was cancelled even after the blackout row is deleted (blackouts
    // are hard-deleted — owner's call, 2026-09-02 — because CLAUDE.md §4.5 is
    // about users and resources, and nothing hangs history off a blackout).
    // The id is included so an operator can still correlate the two while the
    // blackout exists.
    public static string CancellationReason(BlackoutPeriod blackout) =>
        Truncate(
            blackout.Reason is { Length: > 0 } reason
                ? $"Blackout {blackout.Id}: {reason}"
                : $"Blackout {blackout.Id}",
            300);

    // Cancels each booking and returns both halves of what the caller needs: the
    // notifications to state as inserts, and the summaries to put on the wire.
    //
    // Nothing is added to a repository here: this class has no repository, which
    // keeps it unit-testable without a fake and keeps the unit of work entirely
    // in the handler (see IBlackoutPeriodRepository).
    //
    // actorUserId is the admin who created or edited the blackout. It lands on
    // Notifications.CreatedByUserId, which is nullable only because the no-show
    // job has no human actor — here there is one. It deliberately does *not*
    // land on Bookings.CancelledByUserId; see Booking.CancelForBlackout.
    public static BlackoutCascadeResult Apply(
        BlackoutPeriod blackout,
        IReadOnlyList<Booking> bookings,
        Guid actorUserId,
        DateTime nowUtc)
    {
        var reason = CancellationReason(blackout);
        var notifications = new List<Notification>(bookings.Count);
        var cancelled = new List<CancelledBookingSummary>(bookings.Count);

        foreach (var booking in bookings)
        {
            // Snapshotted before the mutation. CancelForBlackout touches none of
            // these four fields today, so this is belt-and-braces — but it keeps
            // the reported summary honest regardless of what that method is
            // later changed to write.
            cancelled.Add(new CancelledBookingSummary(
                booking.Id, booking.UserId, booking.StartsAtUtc, booking.EndsAtUtc, booking.RecurrenceRuleId));

            booking.CancelForBlackout(reason, nowUtc);

            // SendAtUtc = now: a cancellation is news, not a reminder, so it is
            // due as soon as the dispatch job (CLAUDE.md §7) next runs. That job
            // does not exist yet, which is the intended design — the row and
            // UQ_Notifications_Once are what make the eventual send idempotent
            // (FR-9.4 / AC-6).
            //
            // Kind.Cancelled, anchored to the BookingId. Not the dual anchor
            // decision 0008 added: that exists for an occurrence which was never
            // created, and every booking here demonstrably exists.
            notifications.Add(Notification.ForBooking(
                Guid.NewGuid(),
                booking.Id,
                booking.UserId,
                NotificationKind.Cancelled,
                nowUtc,
                actorUserId,
                nowUtc));
        }

        return new BlackoutCascadeResult(cancelled, notifications);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
