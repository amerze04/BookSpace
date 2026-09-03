namespace BookSpace.Application.Features.Resources.GetResourceAvailability;

// One bookable span on the wire. WP-3 decision D2: an interval carrying how many
// units are left, not a boolean and not a fixed grid of slots.
//
// Its own record in its own file, per docs/decisions/0015's amendment — even
// though nothing else currently looks like it. The read detail's
// AvailabilityWindowDetail is the nearest thing and is deliberately different:
// that one is a weekly rule in local wall-clock time, this one is a concrete
// span of real time.
//
// Instants, not local times, and that is not an oversight. The client asked in
// the resource's local dates and is told which zone that was
// (GetResourceAvailabilityQueryResponse.TimeZoneId), but a bookable span has to
// be unambiguous — and on a clocks-back day a local time names two instants, so
// a local-time answer would be the one shape that cannot say what it means.
// Serialized with the trailing Z by the §4.3 Kind convention.
//
// RemainingCapacity is a floor across the whole span: the calculation cuts the
// span wherever the figure changes, so it holds at every instant inside it.
public sealed record BookableIntervalDetail(
    DateTime StartUtc,
    DateTime EndUtc,
    int RemainingCapacity);
