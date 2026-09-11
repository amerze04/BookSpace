namespace BookSpace.Application.Features.RecurrenceRules;

// FR-5.4: "never drop a conflicting occurrence silently." One of these per
// date RecurrenceExpansion produced, so a series-creation response can name
// every occurrence and say what happened to it — never just a count.
//
// Shared between CreateSeries's success response and
// NoOccurrencesCreatedException (Common/Errors), which is why it lives here
// rather than nested under CreateSeries: the all-refused case is the same
// breakdown a successful response carries, just attached to a 422 instead of
// a 201 (decision from wp5-plan.md §5.1's shape question 2).
public enum RecurrenceOccurrenceReportStatus
{
    // dbo.CreateBooking accepted it. BookingId is set.
    Created,

    // Decision 0008: the local start or end fell in a clocks-forward gap.
    // Neither BookingId nor ReasonCode is set — there was nothing to refuse,
    // only nothing to create.
    SkippedSpringForwardGap,

    // BookingEligibility's pre-check, or dbo.CreateBooking itself, refused it.
    // ReasonCode is set; BookingId is not.
    Refused,
}

public sealed record RecurrenceOccurrenceReport(
    DateOnly OccurrenceDate,
    RecurrenceOccurrenceReportStatus Status,
    Guid? BookingId,
    string? ReasonCode)
{
    public static RecurrenceOccurrenceReport ForCreated(DateOnly occurrenceDate, Guid bookingId) =>
        new(occurrenceDate, RecurrenceOccurrenceReportStatus.Created, bookingId, ReasonCode: null);

    public static RecurrenceOccurrenceReport ForSkippedSpringForwardGap(DateOnly occurrenceDate) =>
        new(occurrenceDate, RecurrenceOccurrenceReportStatus.SkippedSpringForwardGap, BookingId: null, ReasonCode: null);

    public static RecurrenceOccurrenceReport ForRefused(DateOnly occurrenceDate, string reasonCode) =>
        new(occurrenceDate, RecurrenceOccurrenceReportStatus.Refused, BookingId: null, reasonCode);
}
