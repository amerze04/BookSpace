using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.RecurrenceRules.CreateSeries;

// FR-5.1 / FR-5.4: create a recurring series, and surface exactly what
// happened to every occurrence rather than dropping any silently.
//
// **The division of labour mirrors CreateBookingCommandRequestHandler's, one
// level up.** RecurrenceExpansion answers "what UTC instant is this
// occurrence, or is there none" (decision 0008/0024); BookingEligibility's
// pre-check over one up-front snapshot answers "would this interval be
// refused, and why" for every occurrence at once; and dbo.CreateBooking,
// called once per eligible occurrence, remains the one authority on capacity
// (CLAUDE.md §4.1). Nothing here re-derives any of those three questions.
//
// **Best-effort, per decision 0007 — never one transaction for the whole
// series.** Each occurrence that reaches the point of being created gets its
// own IUnitOfWork.ExecuteAsync, so one occurrence losing a race, or hitting a
// blackout the pre-check's snapshot had not yet seen, refuses only that
// occurrence. A crash or a deadlock replays one occurrence's attempt, never
// hundreds of them.
public sealed class CreateRecurrenceSeriesCommandRequestHandler
    : IRequestHandler<CreateRecurrenceSeriesCommandRequest, CreateRecurrenceSeriesCommandResponse>
{
    private readonly IAvailabilityRepository _availability;
    private readonly IBookingRepository _bookings;
    private readonly IRecurrenceRuleRepository _recurrenceRules;
    private readonly ITimeZoneCatalog _timeZones;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CreateRecurrenceSeriesCommandRequestHandler(
        IAvailabilityRepository availability,
        IBookingRepository bookings,
        IRecurrenceRuleRepository recurrenceRules,
        ITimeZoneCatalog timeZones,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _availability = availability;
        _bookings = bookings;
        _recurrenceRules = recurrenceRules;
        _timeZones = timeZones;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CreateRecurrenceSeriesCommandResponse> Handle(
        CreateRecurrenceSeriesCommandRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: a recurring series is always created by a specific member (FR-5.1).");

        var resource = await _availability.FindWithScheduleAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        ResourceWriteRules.EnsureNotArchived(resource);

        // Every occurrence shares the same nominal duration, so this is asked
        // once rather than per occurrence.
        var nominalDuration = request.LocalEndTime - request.LocalStartTime;
        if (!resource.AllowsBookingDuration(nominalDuration))
        {
            throw new BookingDurationOutOfRangeException(resource.Id, (int)nominalDuration.TotalMinutes);
        }

        var nowUtc = _clock.UtcNow;

        var rule = new RecurrenceRule(
            Guid.NewGuid(),
            resource.OrgId,
            resource.Id,
            userId,
            request.Frequency,
            request.IntervalValue,
            request.LocalStartTime,
            request.LocalEndTime,
            request.StartDate,
            request.EndDate,
            request.OccurrenceCount,
            resource.TimeZoneId,
            userId,
            nowUtc);

        // Persisted up front: Bookings.RecurrenceRuleId is a real FK, so
        // every occurrence created below needs this row to already exist.
        // If every occurrence turns out to be skipped or refused, the rule
        // is removed again before the 422 is thrown (below) — see that
        // branch and IRecurrenceRuleRepository.Remove for why the delete is
        // safe: nothing else in the database ever comes to reference this
        // row unless an occurrence is actually created.
        _recurrenceRules.Add(rule);
        await _recurrenceRules.SaveChangesAsync(cancellationToken);

        var zone = _timeZones.GetResourceTimeZone(resource.TimeZoneId);
        var occurrences = RecurrenceExpansion.Expand(rule, zone);

        var status = resource.RequiresApproval ? BookingStatus.Pending : BookingStatus.Confirmed;

        // FR-7.4, fetched once: every occurrence belongs to the same tenant,
        // so the expiry configuration cannot differ between them — fetching
        // it per occurrence would be up to hundreds of identical round trips.
        var expiryHours = status == BookingStatus.Pending
            ? await _bookings.FindApprovalExpiryHoursAsync(resource.OrgId, cancellationToken)
            : null;

        // One snapshot read across the whole series' span, not one per
        // occurrence — safe because occurrences of *one* rule never overlap
        // each other in time, so an earlier occurrence in this loop cannot
        // change a later one's eligibility answer. Advisory, exactly as the
        // single-booking handler's pre-check is: dbo.CreateBooking remains
        // the authority per occurrence.
        var span = SeriesSpan(occurrences);
        var blackouts = span is { } blackoutSpan
            ? await _availability.FindBlackoutIntervalsAsync(resource.Id, blackoutSpan, cancellationToken)
            : [];
        var booked = span is { } bookedSpan
            ? await _availability.FindBookedQuantitiesAsync(resource.Id, bookedSpan, cancellationToken)
            : [];

        var reports = new List<RecurrenceOccurrenceReport>(occurrences.Count);

        // Built, not staged: whether these are ever persisted depends on
        // whether the series as a whole reserves anything at all, decided
        // only once the loop finishes (below). Staging them into the
        // DbContext immediately, the way the rule above is added, would mean
        // explicitly undoing an insert already sent to SQL Server in the
        // all-refused branch; keeping them as plain objects until that
        // branch is known means there is nothing to undo — an all-refused
        // series simply never adds them.
        var pendingSkippedNotifications = new List<Notification>();

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Outcome == RecurrenceOccurrenceOutcome.SkippedSpringForwardGap)
            {
                pendingSkippedNotifications.Add(
                    BuildSkippedNotification(rule, occurrence.OccurrenceDate, nowUtc));
                reports.Add(RecurrenceOccurrenceReport.ForSkippedSpringForwardGap(occurrence.OccurrenceDate));
                continue;
            }

            var interval = occurrence.Interval!.Value;

            // Entirely elapsed: the same rule CreateBookingCommandRequestHandler
            // applies to a one-off request, applied per occurrence here.
            if (interval.EndUtc <= nowUtc)
            {
                reports.Add(
                    RecurrenceOccurrenceReport.ForRefused(occurrence.OccurrenceDate, ReasonCodes.BookingInThePast));
                continue;
            }

            var eligibility = BookingEligibility.Evaluate(
                resource, interval, request.Quantity, zone, blackouts, booked);

            if (eligibility != BookingEligibilityResult.Eligible)
            {
                reports.Add(
                    RecurrenceOccurrenceReport.ForRefused(occurrence.OccurrenceDate, ReasonCodeFor(eligibility)));
                continue;
            }

            var (bookingId, reasonCode) = await CreateOccurrenceAsync(
                resource, rule.Id, userId, interval, request.Quantity, request.Title, status, expiryHours,
                nowUtc, cancellationToken);

            reports.Add(bookingId is { } id
                ? RecurrenceOccurrenceReport.ForCreated(occurrence.OccurrenceDate, id)
                : RecurrenceOccurrenceReport.ForRefused(occurrence.OccurrenceDate, reasonCode!));
        }

        if (!reports.Any(r => r.Status == RecurrenceOccurrenceReportStatus.Created))
        {
            // Nothing was reserved. Compensate for the rule persisted up
            // front so the 422 leaves no trace behind it — not the series
            // shell, and not a "your occurrence was skipped" notification
            // for a series that, as far as the client was ever told, never
            // came into being. Safe unconditionally: no Booking exists to
            // reference this rule (that is the condition for reaching this
            // branch at all), and pendingSkippedNotifications was never
            // added to the context, so there is nothing else to undo.
            _recurrenceRules.Remove(rule);
            await _recurrenceRules.SaveChangesAsync(cancellationToken);

            throw new NoOccurrencesCreatedException(reports);
        }

        // At least one occurrence exists, so any spring-forward skips in the
        // same series are genuine and get their 0008 notification — flushed
        // together here rather than per skip, since a skip after the last
        // successful occurrence would otherwise be left staged on nothing.
        if (pendingSkippedNotifications.Count > 0)
        {
            _bookings.AddNotifications(pendingSkippedNotifications);
            await _bookings.SaveChangesAsync(cancellationToken);
        }

        return new CreateRecurrenceSeriesCommandResponse(rule.Id, reports);
    }

    // The smallest interval covering every occurrence RecurrenceExpansion
    // resolved to an instant — null when every occurrence was skipped, which
    // is the one case where there is nothing to pre-check at all.
    private static UtcInterval? SeriesSpan(IReadOnlyList<RecurrenceOccurrence> occurrences)
    {
        UtcInterval? span = null;

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Interval is not { } interval)
            {
                continue;
            }

            span = span is { } existing
                ? new UtcInterval(
                    existing.StartUtc < interval.StartUtc ? existing.StartUtc : interval.StartUtc,
                    existing.EndUtc > interval.EndUtc ? existing.EndUtc : interval.EndUtc)
                : interval;
        }

        return span;
    }

    // Decision 0008: told again 14 days before the occurrence's own date, via
    // the existing Reminder dispatch job — no new job, and the row is
    // anchored to RecurrenceRuleId + OccurrenceDate because there is no
    // Booking to point at.
    //
    // Deliberately just a constructor call, not a save — see the loop above
    // and its trailing flush for why persisting this is deferred until the
    // series' overall outcome is known. Committing it immediately, per skip,
    // was this handler's first design and had a real bug: it left the row in
    // the database even when the series as a whole reserved nothing, which
    // is the orphan this whole compensating structure exists to avoid.
    private static Notification BuildSkippedNotification(RecurrenceRule rule, DateOnly occurrenceDate, DateTime nowUtc)
    {
        var sendAtUtc = DateTime.SpecifyKind(
            occurrenceDate.AddDays(-14).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        return Notification.ForSkippedOccurrence(
            Guid.NewGuid(), rule.Id, occurrenceDate, rule.UserId, sendAtUtc, nowUtc);
    }

    // **Why staging happens outside the delegate, exactly as the single-
    // booking handler does, and why that alone is not enough here.** The
    // booking id, the (unstaged) approval request and the notifications are
    // all built before IUnitOfWork opens — same reasoning as
    // CreateBookingCommandRequestHandler: a 1205 retry re-runs the delegate,
    // and entities Add()-ed *inside* it with a freshly minted id would be
    // added twice.
    //
    // What is different from the single-booking path is that this call site
    // has a *next* occurrence to fall through to. AddApprovalRequest and
    // AddNotifications are therefore only ever invoked *inside* the delegate,
    // after dbo.CreateBooking has confirmed Created — never unconditionally
    // before it, the way the single-booking handler stages them. A declined
    // outcome (SlotUnavailable and so on, no exception) then calls neither,
    // so nothing is left tracked-but-unsaved to leak into the next
    // occurrence's SaveChangesAsync — which would otherwise try to insert an
    // ApprovalRequest against a BookingId that was never created. And a 1205
    // retry of *this* occurrence stages the same pre-built instances again,
    // which EF simply treats as already tracked rather than duplicating.
    private async Task<(Guid? BookingId, string? ReasonCode)> CreateOccurrenceAsync(
        Resource resource,
        Guid recurrenceRuleId,
        Guid userId,
        UtcInterval interval,
        int quantity,
        string? title,
        BookingStatus status,
        int? expiryHours,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var bookingId = Guid.NewGuid();

        var approval = status == BookingStatus.Pending
            ? new ApprovalRequest(
                Guid.NewGuid(),
                bookingId,
                nowUtc,
                expiryHours is null ? null : nowUtc.AddHours(expiryHours.Value))
            : null;

        var notifications = NotificationsFor(resource, bookingId, userId, status, nowUtc);

        return await _unitOfWork.ExecuteAsync(
            async token =>
            {
                var outcome = await _bookings.CreateAsync(
                    new NewBooking(
                        bookingId,
                        resource.Id,
                        userId,
                        recurrenceRuleId,
                        interval.StartUtc,
                        interval.EndUtc,
                        quantity,
                        title,
                        status,
                        CreatedByUserId: userId,
                        nowUtc),
                    token);

                if (outcome.Result != BookingCreationResult.Created)
                {
                    return ((Guid?)null, ReasonCodeFor(outcome));
                }

                if (approval is not null)
                {
                    _bookings.AddApprovalRequest(approval);
                }

                _bookings.AddNotifications(notifications);

                await _bookings.SaveChangesAsync(token);

                return ((Guid?)bookingId, (string?)null);
            },
            cancellationToken);
    }

    // Same asymmetry CreateBookingCommandRequestHandler's own NotificationsFor
    // documents — Confirmed notifies the booker, Pending notifies every
    // approver and not the booker — duplicated rather than shared, on the
    // same footing CreateBookingCommandRequestValidator's header gives for
    // not sharing its own instant rules: two call sites and no third in
    // sight.
    private static IReadOnlyList<Notification> NotificationsFor(
        Resource resource, Guid bookingId, Guid userId, BookingStatus status, DateTime nowUtc)
    {
        if (status == BookingStatus.Confirmed)
        {
            return
            [
                Notification.ForBooking(
                    Guid.NewGuid(), bookingId, userId, NotificationKind.Confirmed, nowUtc, userId, nowUtc),
            ];
        }

        return resource.ApproverUserIds
            .Select(approverId => Notification.ForBooking(
                Guid.NewGuid(), bookingId, approverId, NotificationKind.ApprovalRequested, nowUtc, userId, nowUtc))
            .ToList();
    }

    private static string ReasonCodeFor(BookingEligibilityResult eligibility) => eligibility switch
    {
        BookingEligibilityResult.OutsideAvailability => ReasonCodes.OutsideAvailability,
        BookingEligibilityResult.BlackoutPeriod => ReasonCodes.BlackoutPeriod,
        BookingEligibilityResult.SlotUnavailable => ReasonCodes.SlotUnavailable,
        BookingEligibilityResult.CapacityExceeded => ReasonCodes.CapacityExceeded,
        _ => throw new InvalidOperationException($"Unhandled booking eligibility result '{eligibility}'."),
    };

    private static string ReasonCodeFor(BookingCreationOutcome outcome) => outcome.Result switch
    {
        BookingCreationResult.ResourceNotFound => ReasonCodes.ResourceNotFound,
        BookingCreationResult.ResourceArchived => ReasonCodes.ResourceArchived,
        BookingCreationResult.BlackoutPeriod => ReasonCodes.BlackoutPeriod,
        BookingCreationResult.SlotUnavailable => ReasonCodes.SlotUnavailable,
        BookingCreationResult.CapacityExceeded => ReasonCodes.CapacityExceeded,
        _ => throw new InvalidOperationException($"Unhandled booking creation result '{outcome.Result}'."),
    };
}
