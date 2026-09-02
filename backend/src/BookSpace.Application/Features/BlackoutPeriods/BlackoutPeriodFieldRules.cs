using FluentValidation;

namespace BookSpace.Application.Features.BlackoutPeriods;

// Shape validation shared by CreateBlackoutPeriodCommandRequest and
// UpdateBlackoutPeriodCommandRequest, added by each concrete validator — the
// same pattern as ResourceFieldRules and PagedQueryRules, and for the same
// reason (a base validator class would spend C#'s single inheritance slot on
// this).
//
// The tier-1 constraints are still the floor (CLAUDE.md §6):
// CK_BlackoutPeriods_Interval and the entity's own constructor guard both
// restate the interval rule. These are the message, not the guarantee.
//
// What is deliberately NOT here: whether the interval is entirely in the past.
// That needs the clock, so it is a rule check in the handler carrying
// ReasonCodes.BlackoutPeriodElapsed — see BlackoutPeriodRules.
public static class BlackoutPeriodFieldRules
{
    // Matches BlackoutPeriods.Reason NVARCHAR(300). Restated here so an
    // over-long reason is a 400 naming the field rather than a truncation or a
    // SQL error.
    public const int MaxReasonLength = 300;

    public static void AddBlackoutPeriodFieldRules<TCommand>(this AbstractValidator<TCommand> validator)
        where TCommand : IBlackoutPeriodWriteCommand
    {
        // CK_BlackoutPeriods_Interval, restated as a per-field message so it is
        // a 400 instead of a 500 from the entity's ArgumentException.
        validator.RuleFor(c => c.EndsAtUtc)
            .GreaterThan(c => c.StartsAtUtc)
            .WithMessage("EndsAtUtc must be after StartsAtUtc.");

        // Both columns are datetime2(0), so a sub-second value would be
        // *rounded* on write and the response would disagree with the row a
        // client reads back — the trap CLAUDE.md §4.3 records for IClock.UtcNow,
        // and the one Phase 3 hit on time(0). Rejected rather than truncated,
        // matching how an oversized pageSize is rejected rather than clamped
        // (docs/decisions/0015).
        validator.RuleFor(c => c.StartsAtUtc)
            .Must(BeAWholeNumberOfSeconds)
            .WithMessage("StartsAtUtc must not carry fractional seconds.");

        validator.RuleFor(c => c.EndsAtUtc)
            .Must(BeAWholeNumberOfSeconds)
            .WithMessage("EndsAtUtc must not carry fractional seconds.");

        // This feature is the first in the API to take an instant on the wire,
        // so it is the first that has to say what an instant without a zone
        // means. The answer is: nothing, and it is refused.
        //
        // System.Text.Json maps a trailing Z to DateTimeKind.Utc and an explicit
        // offset to Local (converted correctly), but a bare local-looking
        // timestamp to Unspecified — which a handler can only interpret by
        // guessing a zone, and whose most likely guess is the *server's*. That is
        // precisely the app/database disagreement §4.3 exists to prevent, and it
        // would be silent: the blackout would simply cover the wrong hours. The
        // handlers normalize Local to UTC; Unspecified has nothing to normalize
        // from.
        validator.RuleFor(c => c.StartsAtUtc)
            .Must(CarryAZone)
            .WithMessage("StartsAtUtc must carry a UTC designator or an explicit offset.");

        validator.RuleFor(c => c.EndsAtUtc)
            .Must(CarryAZone)
            .WithMessage("EndsAtUtc must carry a UTC designator or an explicit offset.");

        // Optional: a blackout without a stated reason is legal (the column is
        // nullable), because "the room is unavailable" is sometimes all an admin
        // can say. Length is capped when one is given.
        validator.RuleFor(c => c.Reason)
            .MaximumLength(MaxReasonLength)
            .When(c => c.Reason is not null);
    }

    private static bool BeAWholeNumberOfSeconds(DateTime value) =>
        value.Ticks % TimeSpan.TicksPerSecond == 0;

    private static bool CarryAZone(DateTime value) =>
        value.Kind != DateTimeKind.Unspecified;
}
