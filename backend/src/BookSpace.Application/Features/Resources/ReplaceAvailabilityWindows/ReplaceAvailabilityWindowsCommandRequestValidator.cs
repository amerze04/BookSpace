using FluentValidation;

namespace BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

// Shape only. What is deliberately NOT here is the one rule that needs the set
// rather than a field: overlapping windows on a weekday, which arrives as 409
// OverlappingAvailabilityWindow from AvailabilityWindowRules. A validator can
// only report per-field errors, and "these two are fine individually but
// contradict each other" has no field to hang on.
public sealed class ReplaceAvailabilityWindowsCommandRequestValidator
    : AbstractValidator<ReplaceAvailabilityWindowsCommandRequest>
{
    public ReplaceAvailabilityWindowsCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // NotNull, not NotEmpty. An empty array is a legitimate request meaning
        // "this resource opens at no time at all" — an admin taking a room out
        // of circulation for a rebuild without archiving it. A *missing* array
        // is a malformed payload, and the difference is worth keeping: clearing
        // a schedule should have to be stated, never achieved by omission.
        RuleFor(c => c.Windows)
            .NotNull()
            .WithMessage("Windows is required; send an empty array to clear the schedule.");

        RuleForEach(c => c.Windows).ChildRules(window =>
        {
            // CK_AvailabilityWindows_Weekday BETWEEN 0 AND 6. IsInEnum rather
            // than an explicit range so the message names the enum a client sees
            // on the way out.
            window.RuleFor(w => w.Weekday)
                .IsInEnum();

            // CK_AvailabilityWindows_Window, restated as a per-field message.
            // The constraint and AvailabilityWindow's constructor are still the
            // floor (CLAUDE.md §6) — this is what turns it into a 400 naming the
            // window instead of a 500 from a domain ArgumentException.
            window.RuleFor(w => w.ClosesAt)
                .GreaterThan(w => w.OpensAt)
                .WithMessage("ClosesAt must be after OpensAt.");

            // Both columns are time(0), so a sub-second value would be *rounded*
            // on write and the response would disagree with the row a client
            // reads back — the same trap CLAUDE.md §4.3 records for
            // IClock.UtcNow and datetime2(0). Rejected rather than truncated,
            // matching how an oversized pageSize is rejected rather than clamped
            // (docs/decisions/0015): silently altering a submitted value is the
            // behaviour being avoided in both cases.
            window.RuleFor(w => w.OpensAt)
                .Must(BeAWholeNumberOfSeconds)
                .WithMessage("OpensAt must not carry fractional seconds.");

            window.RuleFor(w => w.ClosesAt)
                .Must(BeAWholeNumberOfSeconds)
                .WithMessage("ClosesAt must not carry fractional seconds.");
        });
    }

    private static bool BeAWholeNumberOfSeconds(TimeOnly value) =>
        value.Ticks % TimeSpan.TicksPerSecond == 0;
}
