using BookSpace.Application.Common.Errors;

namespace BookSpace.Application.Features.BlackoutPeriods;

// The tier-4 rules (CLAUDE.md §6) for blackout writes, in one place so each
// reason code has exactly one thrower — the same shape as ResourceWriteRules.
//
// Short on purpose. The interval's own sanity (EndsAtUtc > StartsAtUtc) is tier
// 1, held by CK_BlackoutPeriods_Interval and restated in BlackoutPeriod's
// constructor; tenant scope is tier 3. What is left is the one thing neither a
// constraint nor the entity can judge, because it needs to know what time it is.
internal static class BlackoutPeriodRules
{
    // FR-3.4. A blackout entirely in the past is refused — see
    // BlackoutPeriodElapsedException for the reasoning, including why the test is
    // on EndsAtUtc and deliberately not on StartsAtUtc.
    //
    // `<=`, not `<`: a blackout ending exactly now is over, and its interval is
    // half-open at the end everywhere else in this feature.
    public static void EnsureNotElapsed(DateTime endsAtUtc, DateTime nowUtc)
    {
        if (endsAtUtc <= nowUtc)
        {
            throw new BlackoutPeriodElapsedException(endsAtUtc, nowUtc);
        }
    }
}
