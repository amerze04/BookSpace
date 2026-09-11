using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.RecurrenceRules.CancelSeries;

// FR-5.3 / decision 0002 reapplied one level up: cancel a recurring series
// and every occurrence it still has a live claim on.
//
// **Not a §4.1 write path**, on exactly CancelBookingCommandRequestHandler's
// reasoning: cancelling can only *reduce* the units a resource holds at an
// instant, never add to it, so there is nothing for dbo.CreateBooking's
// locking protocol to protect. Plain EF, one SaveChangesAsync, no
// IUnitOfWork.
//
// **The reach is the same owner filter the booking reads and cancel use**
// (BookingReadRules.ResolveDetailFilter, reused verbatim rather than
// reimplemented) — decision 0002's rule that "may I see it" and "may I
// cancel it" cannot disagree, applied one level up from a single booking to
// the series that produced it.
public sealed class CancelRecurrenceSeriesCommandRequestHandler
    : IRequestHandler<CancelRecurrenceSeriesCommandRequest, CancelRecurrenceSeriesCommandResponse>
{
    private readonly IRecurrenceRuleRepository _recurrenceRules;
    private readonly IBookingRepository _bookings;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CancelRecurrenceSeriesCommandRequestHandler(
        IRecurrenceRuleRepository recurrenceRules,
        IBookingRepository bookings,
        ICurrentUser currentUser,
        IClock clock)
    {
        _recurrenceRules = recurrenceRules;
        _bookings = bookings;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CancelRecurrenceSeriesCommandResponse> Handle(
        CancelRecurrenceSeriesCommandRequest request,
        CancellationToken cancellationToken)
    {
        // Behind TenantMember, so null is broken wiring rather than a client
        // error — and it would also mean writing a null into
        // RecurrenceRules.UpdatedByUserId, which nothing in this system does
        // for a member-initiated action.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: a cancellation records who performed it (decision 0002).");

        var owner = BookingReadRules.ResolveDetailFilter(
            actorUserId,
            BookingReadRules.CanSeeOtherMembersBookings(_currentUser));

        // Filtered in the query — decision 0025's tenant scoping and the owner
        // filter together mean a series this caller may not reach is never
        // loaded. All cases arrive as null and leave as one indistinguishable
        // 404: no such id, another tenant's id, or another member's series
        // when the caller is not an admin. Never a 403, which would confirm
        // the series exists (RecurrenceRuleNotFoundException).
        var rule = await _recurrenceRules.FindForCancellationAsync(
            request.RecurrenceRuleId, owner, cancellationToken)
            ?? throw new RecurrenceRuleNotFoundException(request.RecurrenceRuleId);

        // Asked as a question rather than caught as an exception, so the
        // client gets a reason code instead of a 500 — Cancel re-checks the
        // same predicate, the domain keeping its own invariant rather than
        // trusting this call site.
        if (!rule.CanBeCancelled())
        {
            throw new RecurrenceRuleNotCancellableException(rule.Id);
        }

        var nowUtc = _clock.UtcNow;

        rule.Cancel(actorUserId, nowUtc);

        // Decision 0002's window (EndsAtUtc > now), reapplied per occurrence:
        // a past or already-terminal occurrence is left exactly as it would
        // be if cancelled individually, never rewritten by this cascade.
        var occurrences = await _bookings.FindOccurrencesToCancelAsync(rule.Id, nowUtc, cancellationToken);

        var reason = request.Reason ?? "Recurring series cancelled";
        foreach (var occurrence in occurrences)
        {
            occurrence.Cancel(actorUserId, reason, nowUtc);
        }

        // Hardening pass, P2: the same invariant CancelBookingCommandRequestHandler
        // enforces for a single booking, applied to every occurrence this
        // cascade just cancelled — a Pending occurrence's approval request
        // must not outlive it as an actionable Pending row.
        var pendingApprovals = await _bookings.FindPendingApprovalRequestsAsync(
            occurrences.Select(o => o.Id).ToList(), cancellationToken);
        foreach (var approval in pendingApprovals)
        {
            approval.Withdraw(nowUtc);
        }

        // One summary notification for the whole series (owner's answer,
        // 2026-09-08), not one per occurrence — addressed to the series
        // owner and suppressed on self-cancel, exactly as the single-booking
        // cancel already does (decision 0002 amendment 4).
        if (actorUserId != rule.UserId)
        {
            _bookings.AddNotifications(
            [
                Notification.ForSeriesCancelled(
                    Guid.NewGuid(), rule.Id, rule.UserId, nowUtc, actorUserId, nowUtc),
            ]);
        }

        // One save covers the rule's status change, every occurrence's, and
        // the notification row — they share the same DbContext, so this is
        // the single SaveChangesAsync CancelBookingCommandRequestHandler's
        // header describes as already being a transaction.
        await _bookings.SaveChangesAsync(cancellationToken);

        return new CancelRecurrenceSeriesCommandResponse(
            rule.Id,
            actorUserId,
            nowUtc,
            occurrences.Select(o => o.Id).ToList());
    }
}
