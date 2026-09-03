namespace BookSpace.Application.Features.Resources;

// The one shape rule the availability query adds, kept beside the other
// Resources rules classes so the number has a home and a reason rather than
// sitting inline in a validator.
//
// Unlike AvailabilityWindowRules and ResourceWriteRules, nothing here throws an
// AppException. That is the point: an over-long range is a malformed *request*,
// not a rule the domain refuses, so it comes back as a plain ValidationFailed 400
// with a field error — the same treatment an oversized pageSize gets
// (docs/decisions/0015-api-contract-and-pagination.md). It is why WP-3 Phase 5
// adds no reason code at all, which is a first for this work package.
internal static class AvailabilityQueryRules
{
    // 90 days (owner's call, 2026-09-03). A cap is needed at all because the
    // expansion is per-date and the response grows with the range: an
    // unbounded request is an invitation to ask for a decade. Rejected rather
    // than clamped, so a client that asks for a year is told, instead of
    // quietly receiving three months and believing it saw everything.
    public const int MaxRangeDays = 90;

    // Inclusive of both ends, so a single date is a range of one day. Stated here
    // rather than as "(To - From).Days <= 89" in a validator, because the
    // off-by-one is the whole difficulty.
    public static int RangeLengthInDays(DateOnly fromLocalDate, DateOnly toLocalDate) =>
        toLocalDate.DayNumber - fromLocalDate.DayNumber + 1;
}
