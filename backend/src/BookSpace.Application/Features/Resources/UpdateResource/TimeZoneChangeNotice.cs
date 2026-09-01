namespace BookSpace.Application.Features.Resources.UpdateResource;

// A machine-readable notice rather than a prose warning string, for the same
// reason a rejection carries a reason code and not a message: the client owns
// the wording, and the count tells it whether the change actually affected
// anything.
//
// Its own file, per the 2026-09-01 convention — it was previously a second
// record sharing UpdateResourceCommandResponse's file.
public sealed record TimeZoneChangeNotice(
    string PreviousTimeZoneId,
    string NewTimeZoneId,
    int ReinterpretedAvailabilityWindowCount);
