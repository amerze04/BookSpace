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

    // Hardening pass. Both bounds exist to keep a malformed request out of
    // RecurrenceRule's constructor and RecurrenceExpansion's loop entirely,
    // rather than to express a product rule of their own — decision 0007's
    // two-year cap is the actual ceiling either ends up enforcing.
    public const int MaxIntervalValue = 366;
    public const int MaxOccurrenceCount = 730;

    // The latest StartDate that cannot, combined with the two bounds above,
    // ever make RecurrenceRule's arithmetic step past DateOnly's own year-9999
    // ceiling — computed rather than a guessed constant, so it stays correct
    // if either bound above changes. Weekly is the worst case among the three
    // frequencies: it multiplies steps by 7 days, where Daily and Monthly
    // multiply by 1.
    private static readonly DateOnly MaxStartDate = ComputeMaxStartDate();

    private static DateOnly ComputeMaxStartDate()
    {
        var maxSteps = (long)MaxIntervalValue * (MaxOccurrenceCount - 1);
        var maxDays = maxSteps * 7;
        var marginYears = (int)(maxDays / 365) + 10; // +10 years of calendar slack

        return DateOnly.MaxValue.AddYears(-marginYears);
    }

    public CreateRecurrenceSeriesCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // **Hardening pass.** Bounds StartDate itself, independently of
        // EndDate/OccurrenceCount: RecurrenceRule.ComputeImpliedEndDate's
        // OccurrenceCount branch calls DateOnly.AddDays/AddMonths on
        // StartDate stepped forward by up to MaxIntervalValue *
        // (MaxOccurrenceCount - 1) days — legal under both bounds above, but
        // still enough to overflow DateOnly's year-9999 ceiling if StartDate
        // itself is already implausibly far in the future. IsWithinMaxSpan
        // only protects the EndDate branch; this protects the other one, and
        // both amount to the same thing IntervalValue/OccurrenceCount's own
        // hardening does — keeping the constructor's arithmetic from ever
        // running past the type it computes in.
        RuleFor(c => c.StartDate)
            .LessThanOrEqualTo(MaxStartDate)
            .WithMessage($"StartDate must not be after {MaxStartDate:yyyy-MM-dd}.");

        // CK_RecurrenceRules_Interval, restated as a per-field message so it
        // is a 400 rather than a 500 from the RecurrenceRule constructor's
        // ArgumentOutOfRangeException.
        //
        // **Hardening pass.** The upper bound is new: nothing previously
        // stopped an IntervalValue large enough that
        // RecurrenceRule.StepDate's underlying DateOnly.AddDays/AddMonths
        // call throws ArgumentOutOfRangeException — unmapped by
        // GlobalExceptionHandler, so a plainly-invalid request reached the
        // client as a 500. 366 comfortably covers "every N days/weeks/months"
        // any admin would plausibly type; decision 0007's two-year cap makes
        // anything larger meaningless regardless, since it could never
        // produce a second occurrence within range. Plain ValidationFailed
        // 400, no new reason code — the same precedent decision 0015 sets for
        // an over-long availability range: this is a malformed request, not a
        // domain refusal.
        RuleFor(c => c.IntervalValue)
            .InclusiveBetween(1, MaxIntervalValue)
            .WithMessage($"IntervalValue must be between 1 and {MaxIntervalValue}.");

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

        // **Hardening pass.** The upper bound is new, for the same reason as
        // IntervalValue's: an unbounded OccurrenceCount lets
        // RecurrenceExpansion.Expand loop far past anything reasonable before
        // either exhausting memory or hitting the same DateOnly overflow.
        // 730 is decision 0007's own two-year cap expressed as the ceiling of
        // "daily, every day, for two years" — comfortably above any legitimate
        // weekly/monthly count for the same span.
        RuleFor(c => c.OccurrenceCount)
            .InclusiveBetween(1, MaxOccurrenceCount)
            .When(c => c.OccurrenceCount is not null)
            .WithMessage($"OccurrenceCount must be between 1 and {MaxOccurrenceCount}.");

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
}
