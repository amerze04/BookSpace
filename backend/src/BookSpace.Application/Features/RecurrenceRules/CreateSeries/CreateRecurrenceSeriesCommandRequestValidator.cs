using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Features.RecurrenceRules.CreateSeries;

// Shape only. Everything that needs the resource, the clock, the calendar or
// the resource's own timezone is a rule check in the handler carrying a
// reason code — duration, availability, blackouts and capacity all depend on
// state this validator cannot see, exactly as CreateBookingCommandRequestValidator's
// header explains for the single-booking path.
public sealed class CreateRecurrenceSeriesCommandRequestValidator
    : AbstractValidator<CreateRecurrenceSeriesCommandRequest>
{
    // Matches Bookings.Title NVARCHAR(200) — every occurrence shares this
    // title (wp5-plan.md §5.1, smaller call 2), so it is validated once here
    // rather than once per occurrence.
    public const int MaxTitleLength = 200;

    // Bug fix (item 12): this file used to bound IntervalValue and
    // OccurrenceCount separately (<= 366, <= 730) as a proxy for decision
    // 0007's two-year cap, applied to one field at a time rather than to what
    // the two actually imply together — so a perfectly legal series (Daily,
    // IntervalValue 400, OccurrenceCount 2: two occurrences 400 days apart,
    // comfortably inside two years) was rejected solely because 400 > 366,
    // never because it actually violated the cap. IsOccurrenceCountWithinMaxSpan
    // below replaces that proxy with the real check — the identical
    // arithmetic RecurrenceRule's own constructor uses for
    // CK_RecurrenceRules_MaxSpan — so what gets rejected is now exactly "the
    // span this implies is too long", nothing narrower and nothing looser.
    // Each field still gets its own GreaterThan(0), restating the domain's
    // positivity guard as a 400 rather than the constructor's
    // ArgumentOutOfRangeException.

    // The latest StartDate the EndDate-bound path's IsWithinMaxSpan can
    // safely call AddYears(2) against without itself overflowing DateOnly —
    // a fixed, generous margin, not derived from IntervalValue/OccurrenceCount
    // now that the real ceiling is the span check below rather than either of
    // those fields alone.
    private static readonly DateOnly MaxStartDate = DateOnly.MaxValue.AddYears(-10);

    public CreateRecurrenceSeriesCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // **Hardening pass.** Bounds StartDate itself, independently of
        // EndDate/OccurrenceCount: a StartDate already implausibly close to
        // DateOnly's year-9999 ceiling would make IsWithinMaxSpan and
        // IsOccurrenceCountWithinMaxSpan's own AddYears(2)/AddDays/AddMonths
        // calls the thing that overflows, rather than the domain constructor
        // they exist to keep from ever being reached with bad input.
        RuleFor(c => c.StartDate)
            .LessThanOrEqualTo(MaxStartDate)
            .WithMessage($"StartDate must not be after {MaxStartDate:yyyy-MM-dd}.");

        // CK_RecurrenceRules_Interval, restated as a per-field message so it
        // is a 400 rather than a 500 from the RecurrenceRule constructor's
        // ArgumentOutOfRangeException. No upper bound here any more (item 12)
        // — IsOccurrenceCountWithinMaxSpan below is what actually protects
        // RecurrenceRule.StepDate's DateOnly.AddDays/AddMonths call from
        // overflow, and it does so from the real two-year cap rather than a
        // guess at this one field's plausible range.
        RuleFor(c => c.IntervalValue)
            .GreaterThan(0)
            .WithMessage("IntervalValue must be greater than zero.");

        // Not backed by a DB constraint (RecurrenceRule.cs explains why) but
        // restated here for the same reason as the interval: a 400 naming the
        // field, not a 500 from the constructor's guard.
        RuleFor(c => c.LocalEndTime)
            .GreaterThan(c => c.LocalStartTime)
            .WithMessage("LocalEndTime must be after LocalStartTime.");

        // CK_RecurrenceRules_EndCondition: exactly one of EndDate or
        // OccurrenceCount.
        RuleFor(c => c)
            .Must(c => (c.EndDate is null) != (c.OccurrenceCount is null))
            .WithMessage("Exactly one of EndDate or OccurrenceCount must be set.")
            .OverridePropertyName("EndDate");

        RuleFor(c => c.OccurrenceCount)
            .GreaterThan(0)
            .When(c => c.OccurrenceCount is not null)
            .WithMessage("OccurrenceCount must be greater than zero.");

        // Bug fix (item 12). Decision 0007's two-year span cap, restated for
        // the OccurrenceCount-bound path exactly as IsWithinMaxSpan restates
        // it below for the EndDate-bound one: without this, an IntervalValue/
        // OccurrenceCount pair whose implied span runs past the cap reaches
        // RecurrenceRule's constructor unvalidated and throws (an
        // ArgumentException for a span that merely exceeds the cap, or —
        // before either field had any bound at all — an unmapped
        // OverflowException from the constructor's own checked arithmetic if
        // the pair was large enough to overflow int first). Both surface as a
        // 500; this makes either case the same 400 IsWithinMaxSpan already
        // gives the EndDate path, using the identical arithmetic
        // RecurrenceRule.ComputeImpliedEndDate uses so the two can never
        // disagree about what "within two years" means.
        RuleFor(c => c)
            .Must(c => IsOccurrenceCountWithinMaxSpan(c.Frequency, c.IntervalValue, c.StartDate, c.OccurrenceCount!.Value))
            .When(c => c.OccurrenceCount is not null && c.IntervalValue > 0)
            .WithMessage("OccurrenceCount and IntervalValue must not imply a span of more than two years.")
            .OverridePropertyName("OccurrenceCount");

        RuleFor(c => c.EndDate)
            .GreaterThanOrEqualTo(c => c.StartDate)
            .When(c => c.EndDate is not null)
            .WithMessage("EndDate must not be before StartDate.");

        // **Hardening pass.** Decision 0007's two-year span cap restated as a
        // field-level 400, the same reasoning IntervalValue and
        // LocalEndTime/LocalStartTime already restate their own DB/domain
        // constraints here: without this, an EndDate far enough beyond the cap
        // reaches RecurrenceRule's constructor unvalidated and throws a plain
        // ArgumentException, which GlobalExceptionHandler maps to 500 rather
        // than the 422 this rule already produces for a same-shaped request
        // that merely triggers the constructor's own check.
        //
        // IsWithinMaxSpan, not a bare `c.StartDate.AddYears(2)` comparison:
        // DateOnly.AddYears throws when the result falls outside year 1-9999,
        // and a client-supplied StartDate near DateOnly.MaxValue would then
        // make evaluating *this validation rule* throw an unhandled exception
        // — the same 500-from-unvalidated-input class this whole file exists
        // to close, just moved one level up into the validator itself.
        RuleFor(c => c)
            .Must(c => IsWithinMaxSpan(c.StartDate, c.EndDate))
            .When(c => c.EndDate is not null)
            .WithMessage("EndDate must not be more than two years after StartDate.")
            .OverridePropertyName("EndDate");

        // CK_Bookings_Quantity, restated exactly as CreateBookingCommandRequestValidator
        // does — no upper bound here either, for the same reason: what is too
        // many depends on the resource's Capacity, which this cannot see.
        RuleFor(c => c.Quantity)
            .GreaterThan(0)
            .WithMessage("Quantity must be greater than zero.");

        RuleFor(c => c.Title)
            .MaximumLength(MaxTitleLength)
            .When(c => c.Title is not null);

        // Matches RecurrenceCreationOperations.IdempotencyKey NVARCHAR(200).
        RuleFor(c => c.IdempotencyKey)
            .MaximumLength(200)
            .When(c => c.IdempotencyKey is not null);
    }

    // Guards the overflow case explicitly rather than letting DateOnly.AddYears
    // throw: a StartDate within two years of DateOnly.MaxValue makes the safe
    // comparison "is EndDate too far out" collapse to "yes" without ever
    // needing to compute a date past the type's own range.
    private static bool IsWithinMaxSpan(DateOnly startDate, DateOnly? endDate)
    {
        if (endDate is null)
        {
            return true;
        }

        if (startDate.Year > DateOnly.MaxValue.Year - 2)
        {
            return false;
        }

        return endDate.Value <= startDate.AddYears(2);
    }

    // Bug fix (item 12). The OccurrenceCount-bound counterpart to
    // IsWithinMaxSpan above, computing the same implied end date
    // RecurrenceRule.ComputeImpliedEndDate does (last occurrence is
    // IntervalValue * (OccurrenceCount - 1) steps after StartDate) — one
    // implementation given two names because the domain type cannot itself
    // be constructed to answer this without partly duplicating this method,
    // the same trade CreateBookingCommandRequestValidator's own header
    // accepts for its own pre-checks.
    //
    // The multiplication is done in long, not int: IntervalValue and
    // OccurrenceCount are both plain int fields, so their product always
    // fits in a long without overflowing — it is *that* multiplication,
    // done in unchecked int space, that RecurrenceRule.ComputeImpliedEndDate
    // wraps in `checked` specifically because it can silently wrap around in
    // a plain int. Doing it here in long space instead means a span large
    // enough to matter is caught by the day-count guard below before ever
    // reaching DateOnly arithmetic, rather than relying on that `checked`
    // block to convert a wraparound into a thrown OverflowException this
    // validator would then have to also catch.
    private static bool IsOccurrenceCountWithinMaxSpan(
        RecurrenceFrequency frequency, int intervalValue, DateOnly startDate, int occurrenceCount)
    {
        if (startDate.Year > DateOnly.MaxValue.Year - 2)
        {
            return false;
        }

        var steps = (long)intervalValue * (occurrenceCount - 1);

        // Weekly's steps are weeks, not days (RecurrenceRule.StepDate
        // multiplies by 7 internally) — converting to an implied day count
        // first is what makes this guard mean the same "two years" regardless
        // of frequency, rather than "two years of Daily steps but a much
        // longer span of Weekly ones".
        var impliedDays = frequency == RecurrenceFrequency.Weekly ? steps * 7 : steps;

        // A day count comfortably wider than any real two-year cap could ever
        // allow — checked before calling AddDays/AddMonths so a pair large
        // enough to overflow those never reaches them. Monthly steps are
        // months, not days, so this is intentionally loose for that
        // frequency; the exact check below is what actually decides Monthly.
        if (impliedDays < 0 || impliedDays > 10_000)
        {
            return false;
        }

        var impliedEndDate = frequency switch
        {
            RecurrenceFrequency.Daily => startDate.AddDays((int)steps),
            RecurrenceFrequency.Weekly => startDate.AddDays((int)steps * 7),
            RecurrenceFrequency.Monthly => startDate.AddMonths((int)steps),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
        };

        return impliedEndDate <= startDate.AddYears(2);
    }
}
