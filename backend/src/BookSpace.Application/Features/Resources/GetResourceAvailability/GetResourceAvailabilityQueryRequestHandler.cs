using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Availability;

namespace BookSpace.Application.Features.Resources.GetResourceAvailability;

// WP-3 Phase 5. Thin on purpose: the calculation lives in
// AvailabilityCalculator, in BookSpace.Domain, because WP-4's booking rejections
// (OutsideAvailability, BlackoutPeriod, CapacityExceeded) ask the same question
// of a single interval and must not reimplement it — see that class's header.
// What is left here is loading, ordering and mapping.
//
// Three queries, and no more regardless of how long the range is. That is what
// the PRD's only endpoint-specific NFR asks for: "availability queries and
// calendar views remain responsive with realistic data volumes (hundreds of
// bookings per resource)". Everything after the third query is in memory, over
// one resource's rows.
public sealed class GetResourceAvailabilityQueryRequestHandler
    : IRequestHandler<GetResourceAvailabilityQueryRequest, GetResourceAvailabilityQueryResponse>
{
    private readonly IAvailabilityRepository _availability;
    private readonly ITimeZoneCatalog _timeZones;

    public GetResourceAvailabilityQueryRequestHandler(
        IAvailabilityRepository availability,
        ITimeZoneCatalog timeZones)
    {
        _availability = availability;
        _timeZones = timeZones;
    }

    public async Task<GetResourceAvailabilityQueryResponse> Handle(
        GetResourceAvailabilityQueryRequest request,
        CancellationToken cancellationToken)
    {
        // The 404 path is also the cross-tenant path: §4.2's filters make another
        // tenant's real id return null, and ResourceNotFound is the only thing
        // this may say about it (AC-4).
        var resource = await _availability.FindWithScheduleAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // An archived resource is readable and bookable by nobody (FR-3.5), so the
        // honest answer is an empty list with IsArchived set — not 422
        // ResourceArchived, which is a write-side refusal. Short-circuited before
        // the other two queries, since neither could change the answer.
        if (resource.IsArchived)
        {
            return Respond(request, resource.TimeZoneId, isArchived: true, []);
        }

        // Throws TimeZoneNotFoundException — a 500 — if the stored id no longer
        // resolves. Correctly so: it passed IsKnownIanaId when it was written, so
        // failure here means the host's tzdata changed, which is not something a
        // client did or can fix (see ITimeZoneCatalog).
        var zone = _timeZones.GetResourceTimeZone(resource.TimeZoneId);

        // The UTC bounds of the requested local dates, computed by the same Domain
        // code the expansion uses, so the rows fetched and the windows expanded
        // cannot be scoped to different spans.
        var span = AvailabilityWindowExpansion.LocalDateRangeToUtc(
            request.FromLocalDate, request.ToLocalDate, zone);

        var blackouts = await _availability.FindBlackoutIntervalsAsync(
            resource.Id, span, cancellationToken);

        var bookings = await _availability.FindBookedQuantitiesAsync(
            resource.Id, span, cancellationToken);

        var bookable = AvailabilityCalculator.BookableIntervals(
            resource, request.FromLocalDate, request.ToLocalDate, zone, blackouts, bookings);

        // Already ordered by start and non-overlapping — every step of the
        // calculation preserves that — so no sort is needed here. Mapped by hand,
        // per docs/decisions/0015.
        return Respond(
            request,
            resource.TimeZoneId,
            isArchived: false,
            bookable
                .Select(i => new BookableIntervalDetail(
                    i.Interval.StartUtc, i.Interval.EndUtc, i.RemainingCapacity))
                .ToList());
    }

    private static GetResourceAvailabilityQueryResponse Respond(
        GetResourceAvailabilityQueryRequest request,
        string timeZoneId,
        bool isArchived,
        IReadOnlyCollection<BookableIntervalDetail> intervals) =>
        new(
            request.ResourceId,
            timeZoneId,
            request.FromLocalDate,
            request.ToLocalDate,
            isArchived,
            intervals);
}
