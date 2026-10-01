using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Jobs;

// Hardening pass, finding 8. Data annotations validate one property at a
// time; this configuration has one rule that spans two:
// NotificationRepository.ClaimDueAsync computes
// `CAST(POWER(2, Attempts) * @BackoffBaseSeconds AS BIGINT)` inside its own
// claim query, for every row whose Attempts is at least 1. BIGINT's range is
// roughly 9.2e18 — `[Range(1, 100)]` on MaxAttempts and
// `[Range(1, int.MaxValue)]` on BackoffBaseSeconds each look reasonable in
// isolation, but a configuration using both extremes (MaxAttempts near 100,
// a large BackoffBaseSeconds) computes a value many orders of magnitude past
// that range — an arithmetic-overflow error thrown *inside the claim
// statement itself* the first time a persistently-failing row's Attempts
// climbs high enough to hit it, taking down that tick's whole claim (every
// row in the batch, not just the bad one) rather than the one notification
// that was actually misbehaving.
//
// A generous but genuinely safe ceiling, left well below BIGINT's actual
// limit: no real retry policy needs a backoff of 1e15 seconds (tens of
// millions of years), so refusing anything that computes past that at boot
// costs nothing a legitimate configuration would ever want.
public sealed class NotificationDispatchOptionsValidator : IValidateOptions<NotificationDispatchOptions>
{
    private const double MaxSafeWorstCaseBackoffSeconds = 1_000_000_000_000_000d; // 1e15

    public ValidateOptionsResult Validate(string? name, NotificationDispatchOptions options)
    {
        var worstCaseBackoffSeconds = Math.Pow(2, options.MaxAttempts) * options.BackoffBaseSeconds;

        if (worstCaseBackoffSeconds > MaxSafeWorstCaseBackoffSeconds)
        {
            return ValidateOptionsResult.Fail(
                $"{NotificationDispatchOptions.SectionName}: MaxAttempts ({options.MaxAttempts}) combined "
                + $"with BackoffBaseSeconds ({options.BackoffBaseSeconds}) computes a worst-case backoff "
                + "(2^MaxAttempts * BackoffBaseSeconds) large enough to risk a BIGINT overflow inside "
                + "NotificationRepository.ClaimDueAsync's own claim query — lower one or both.");
        }

        return ValidateOptionsResult.Success;
    }
}
