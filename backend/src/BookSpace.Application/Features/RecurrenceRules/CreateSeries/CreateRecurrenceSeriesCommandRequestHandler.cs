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

        // Persisted up front, unconditionally: Bookings.RecurrenceRuleId is a
        // real FK, so every occurrence created below needs this row to
        // already exist. Accepted consequence, not settled either way by
        // wp5-plan.md's architecture: if every occurrence is skipped or
        // refused, this row survives the resulting 422 as an orphaned,
        // Active series with nothing booked against it — flagged here rather
        // than decided silently (CLAUDE.md §11).
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

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Outcome == RecurrenceOccurrenceOutcome.SkippedSpringForwardGap)
            {
                await EnqueueSkippedNotificationAsync(rule, occurrence.OccurrenceDate, nowUtc, cancellationToken);
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
            throw new NoOccurrencesCreatedException(reports);
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
    // Booking to point at. Its own trivial save rather than folded into a
    // neighbouring occurrence's transaction: a gap is at most twice a year
    // per resource, so batching would save a rare round trip at the cost of
    // real complexity (see CreateOccurrenceAsync's header for why staging has
    // to be handled carefully once retries are involved).
    private async Task EnqueueSkippedNotificationAsync(
        RecurrenceRule rule, DateOnly occurrenceDate, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var sendAtUtc = DateTime.SpecifyKind(
            occurrenceDate.AddDays(-14).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        _bookings.AddNotifications(
        [
            Notification.ForSkippedOccurrence(
                Guid.NewGuid(), rule.Id, occurrenceDate, rule.UserId, sendAtUtc, nowUtc),
        ]);

        await _bookings.SaveChangesAsync(cancellationToken);
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
