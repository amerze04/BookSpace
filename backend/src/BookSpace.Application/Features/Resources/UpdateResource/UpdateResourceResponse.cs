using BookSpace.Application.Features.Resources.GetResource;

namespace BookSpace.Application.Features.Resources.UpdateResource;

// PUT /resources/{id}. The updated resource, plus the one thing an edit can do
// that the resource's own fields do not show.
//
// wp3-plan's "smaller calls": changing TimeZoneId **reinterprets** every
// existing availability window rather than shifting it, because a window is
// stored as resource-local wall-clock time (docs/decisions/0003) — 09:00–17:00
// stays 09:00–17:00 and starts meaning a different instant. That is a
// defensible choice but a surprising one, so the response says it happened
// instead of leaving the admin to discover it from a booking that lands an hour
// off. Null whenever the timezone did not change, which is the normal case.
public sealed record UpdateResourceResponse(
    ResourceDetailResponse Resource,
    TimeZoneChangeNotice? TimeZoneChange);

// A machine-readable notice rather than a prose warning string, for the same
// reason a rejection carries a reason code and not a message: the client owns
// the wording, and the count tells it whether the change actually affected
// anything.
public sealed record TimeZoneChangeNotice(
    string PreviousTimeZoneId,
    string NewTimeZoneId,
    int ReinterpretedAvailabilityWindowCount);
