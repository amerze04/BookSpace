using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.CreateBooking;

// FR-4.1 / FR-4.3 / FR-4.5: create a one-off booking, or say precisely why not.
//
// **The division of labour is the whole design here.** The rules that depend on
// data nobody else is writing — availability, the duration limits, whether the
// interval has elapsed — are answered in this handler, so the client gets a
// specific reason. The one rule that races, capacity, is answered by
// dbo.CreateBooking under a UPDLOCK/HOLDLOCK range lock, and *that* answer is
// the authoritative one (CLAUDE.md §4.1,
// docs/decisions/0023-booking-concurrency-strategy.md). The pre-check reports
// what was true a moment ago; the procedure decides.
//
// So capacity is genuinely checked twice, and deliberately. Dropping the
// pre-check would still be correct, and would still never double-book — but a
// booker asking for a slot that is plainly full would get the same bare 409 as
// one who lost a race by a millisecond, and the API would stop being able to
// explain itself (FR-4.5).
//
// Reads use IAvailabilityRepository — the *same three queries* the availability
// endpoint makes, feeding the same Domain code. That is not a convenience: it is
// what makes "the endpoint offered me this slot" and "the endpoint accepted my
// booking" the same question, which is the failure AvailabilityCalculator's
// header exists to prevent.
public sealed class CreateBookingCommandRequestHandler
    : IRequestHandler<CreateBookingCommandRequest, CreateBookingCommandResponse>
{
    private readonly IAvailabilityRepository _availability;
    private readonly IBookingRepository _bookings;
    private readonly ITimeZoneCatalog _timeZones;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CreateBookingCommandRequestHandler(
        IAvailabilityRepository availability,
        IBookingRepository bookings,
        ITimeZoneCatalog timeZones,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _availability = availability;
        _bookings = bookings;
        _timeZones = timeZones;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CreateBookingCommandResponse> Handle(
        CreateBookingCommandRequest request,
        CancellationToken cancellationToken)
    {
        // An InvalidOperationException, not an AppException, for the reason the
        // other write handlers give: the endpoint sits behind a policy requiring
        // an authenticated tenant principal, so null here means broken wiring —
        // a 500, not a reason code a client could act on.
        var userId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: a booking is always made by a specific member (FR-4.1).");

        // Normalized before anything reads them. The validator has already
        // refused DateTimeKind.Unspecified, so this only ever converts a
        // client-supplied offset; a UTC value is returned unchanged.
        var startsAtUtc = request.StartsAtUtc.ToUniversalTime();
        var endsAtUtc = request.EndsAtUtc.ToUniversalTime();
        var requested = new UtcInterval(startsAtUtc, endsAtUtc);

        // Tenant-filtered, so another tenant's real resource id arrives as null
        // and leaves as ResourceNotFound — the same answer an id that exists
        // nowhere gets (AC-4). No OrgId comparison is written here, deliberately:
        // CLAUDE.md §4.2's point is that isolation must not depend on a handler
        // remembering one.
        var resource = await _availability.FindWithScheduleAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // The rejection order is fixed and asserted (see BookingEligibility for
        // why): cheapest and most structural first, the one needing a lock last.
        // FR-3.5 — an archived resource keeps its history and refuses new
        // bookings, which is the same rule that refuses an edit.
        ResourceWriteRules.EnsureNotArchived(resource);

        // The duration limits live on the aggregate and are never re-derived
        // here. AllowsBookingDuration applies both bounds, unlike CanFitABooking
        // which the availability query uses to measure a *span* a booking would
        // be taken out of.
        if (!resource.AllowsBookingDuration(requested.Duration))
        {
            throw new BookingDurationOutOfRangeException(
                resource.Id, (int)requested.Duration.TotalMinutes);
        }

        var nowUtc = _clock.UtcNow;

        // Entirely elapsed, so it could reserve nothing. The test is on the end
        // and deliberately not the start — decision 0019's rule, reapplied:
        // booking the room you are already sitting in is the ordinary case.
        if (endsAtUtc <= nowUtc)
        {
            throw new BookingInThePastException(resource.Id, endsAtUtc);
        }

        // Throws TimeZoneNotFoundException — a 500 — if the stored id no longer
        // resolves. Correctly so: it passed IsKnownIanaId when it was written, so
        // failure here means the host's tzdata changed (see ITimeZoneCatalog).
        var zone = _timeZones.GetResourceTimeZone(resource.TimeZoneId);

        // Scoped to the requested interval rather than to the whole day. Nothing
        // outside it can change whether *it* is covered, and a narrower span is a
        // narrower read.
        var blackouts = await _availability.FindBlackoutIntervalsAsync(
            resource.Id, requested, cancellationToken);

        var booked = await _availability.FindBookedQuantitiesAsync(
            resource.Id, requested, cancellationToken);

        EnsureEligible(
            BookingEligibility.Evaluate(
                resource, requested, request.Quantity, zone, blackouts, booked),
            resource.Id);

        var requestedStatus = resource.RequiresApproval ? BookingStatus.Pending : BookingStatus.Confirmed;

        // **Everything below is staged before the unit of work opens**, and the
        // id is minted here rather than inside the delegate. Both are about the
        // retry: IUnitOfWork re-runs its delegate after a deadlock (1205), so an
        // id minted inside would insert a second booking, and entities added
        // inside would be added twice — the rollback discards the rows but not
        // the ChangeTracker entries, and the second attempt would then try to
        // insert two ApprovalRequests against UQ_ApprovalRequests_Booking.
        // Staging once, outside, makes the delegate safe to repeat.
        var bookingId = Guid.NewGuid();

        // Hardening pass, P1: dbo.CreateBooking may downgrade a requested
        // Confirmed to Pending if RequiresApproval is true when it reads the
        // resource under its own lock — this snapshot was read a moment
        // earlier and can be stale. Both possible outcomes are therefore
        // staged as plain objects (fixed ids, never inserted twice on a 1205
        // retry) before the delegate opens, and only one is actually Add()-ed
        // inside it, once the procedure's ActualStatus says which happened.
        // expiryHours is fetched unconditionally for the same reason: if the
        // downgrade fires, FR-7.4's configured expiry still has to apply, and
        // fetching it late, inside the delegate, would be a read whose result
        // feeds a freshly-minted ApprovalRequest id — exactly the double-insert
        // hazard the comment above exists to avoid. The cost is one extra cheap
        // read on the ordinary Confirmed path.
        var expiryHours = await _bookings.FindApprovalExpiryHoursAsync(resource.OrgId, cancellationToken);
        var approval = new ApprovalRequest(
            Guid.NewGuid(),
            bookingId,
            nowUtc,
            expiryHours is null ? null : nowUtc.AddHours(expiryHours.Value));
        var approvalDetail = new BookingApprovalDetail(approval.Id, approval.ExpiresAtUtc);

        var confirmedNotifications = NotificationsFor(resource, bookingId, userId, BookingStatus.Confirmed, nowUtc);
        var pendingNotifications = NotificationsFor(resource, bookingId, userId, BookingStatus.Pending, nowUtc);

        return await _unitOfWork.ExecuteAsync(
            async token =>
            {
                var outcome = await _bookings.CreateAsync(
                    new NewBooking(
                        bookingId,
                        resource.Id,
                        userId,
                        RecurrenceRuleId: null, // WP-5 materializes series occurrences
                        startsAtUtc,
                        endsAtUtc,
                        request.Quantity,
                        request.Title,
                        requestedStatus,
                        CreatedByUserId: userId,
                        nowUtc),
                    token);

                if (outcome.Result != BookingCreationResult.Created)
                {
                    throw Rejection(outcome, resource.Id);
                }

                var actualStatus = outcome.ActualStatus
                    ?? throw new InvalidOperationException(
                        "dbo.CreateBooking reported Created with no ActualStatus.");

                // Staged only now that the real status is known, so a Pending
                // booking always gets exactly one ApprovalRequest and the
                // right notifications — never the ones built for the status
                // this handler merely guessed at.
                if (actualStatus == BookingStatus.Pending)
                {
                    _bookings.AddApprovalRequest(approval);
                    _bookings.AddNotifications(pendingNotifications);
                }
                else
                {
                    _bookings.AddNotifications(confirmedNotifications);
                }

                // The approval request and the notifications, in the same
                // transaction as the booking the procedure just inserted. A
                // notification without its booking, or a pending booking with no
                // decision record for an approver to find, would each be a state
                // nothing in the system could repair.
                await _bookings.SaveChangesAsync(token);

                return new CreateBookingCommandResponse(
                    bookingId,
                    resource.Id,
                    userId,
                    startsAtUtc,
                    endsAtUtc,
                    request.Quantity,
                    request.Title,
                    actualStatus,
                    nowUtc,
                    actualStatus == BookingStatus.Pending ? approvalDetail : null);
            },
            cancellationToken);
    }

    // FR-8.1, rows only — nothing sends anything yet (CLAUDE.md §7), and
    // UQ_Notifications_Once is what will make the eventual send idempotent.
    // Same arrangement BlackoutCascade already uses.
    //
    // Which rows depends on the status, and the asymmetry is the point:
    //
    //   Confirmed — one row to the booker. It is the confirmation FR-8.1 names,
    //   and it goes to them even though they just made the booking themselves,
    //   because it is the artefact they keep: the details, and later the ICS
    //   attachment (FR-8.2).
    //
    //   Pending — one row per approver, and **none to the booker**. There is
    //   nothing to confirm yet, and the create response has already told them the
    //   status. FR-7.3 puts the member's notification at the decision, which is
    //   WP-5's.
    //
    // SendAtUtc = now: both kinds are news rather than reminders, so they are due
    // as soon as the dispatch job next runs. Reminder rows (FR-8.3) are
    // deliberately not written here — see docs/wp4-plan.md.
    private static IReadOnlyList<Notification> NotificationsFor(
        Resource resource,
        Guid bookingId,
        Guid userId,
        BookingStatus status,
        DateTime nowUtc)
    {
        if (status == BookingStatus.Confirmed)
        {
            return
            [
                Notification.ForBooking(
                    Guid.NewGuid(), bookingId, userId, NotificationKind.Confirmed, nowUtc, userId, nowUtc),
            ];
        }

        // One per approver. The rows differ by RecipientUserId, so
        // UQ_Notifications_Once admits all of them. The list cannot be empty: a
        // resource with RequiresApproval and no approvers is refused at both ends
        // by ApproversRequired (WP-3 Phase 3), so this is not a case to defend
        // against here.
        return resource.ApproverUserIds
            .Select(approverId => Notification.ForBooking(
                Guid.NewGuid(),
                bookingId,
                approverId,
                NotificationKind.ApprovalRequested,
                nowUtc,
                userId,
                nowUtc))
            .ToList();
    }

    // The pre-check's answer, as the exception the client sees. One arm per
    // reason so a new BookingEligibilityResult member cannot be added without
    // deciding what it means on the wire.
    private static void EnsureEligible(BookingEligibilityResult eligibility, Guid resourceId)
    {
        switch (eligibility)
        {
            case BookingEligibilityResult.Eligible:
                return;
            case BookingEligibilityResult.OutsideAvailability:
                throw new OutsideAvailabilityException(resourceId);
            case BookingEligibilityResult.BlackoutPeriod:
                throw new BookingInBlackoutPeriodException(resourceId);
            case BookingEligibilityResult.SlotUnavailable:
                throw new SlotUnavailableException(resourceId);
            case BookingEligibilityResult.CapacityExceeded:
                // No figure to report: the pre-check answers a containment
                // question, not "how many are left". The procedure's throw below
                // carries the number.
                throw new CapacityExceededException(resourceId, remainingCapacity: null);
            default:
                throw new InvalidOperationException(
                    $"Unhandled booking eligibility result '{eligibility}'.");
        }
    }

    // The procedure's answer, as the exception the client sees.
    //
    // ResourceNotFound and ResourceArchived are reachable here even though the
    // handler checked both a moment ago — the resource can be archived in
    // between, and a missing tenant session context surfaces as ResourceNotFound
    // by design (see 0023's fail-closed guard). Mapping them rather than treating
    // them as impossible is what keeps that guard a 404 instead of a 500.
    private static AppException Rejection(BookingCreationOutcome outcome, Guid resourceId) =>
        outcome.Result switch
        {
            BookingCreationResult.ResourceNotFound => new ResourceNotFoundException(resourceId),
            BookingCreationResult.ResourceArchived => new ResourceArchivedException(resourceId),
            BookingCreationResult.BlackoutPeriod => new BookingInBlackoutPeriodException(resourceId),
            BookingCreationResult.SlotUnavailable => new SlotUnavailableException(resourceId),
            BookingCreationResult.CapacityExceeded =>
                new CapacityExceededException(resourceId, outcome.RemainingCapacity),
            _ => throw new InvalidOperationException(
                $"Unhandled booking creation result '{outcome.Result}'."),
        };
}
