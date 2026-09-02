using BookSpace.Application.Common.Errors;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Resources;

// The tier-4 rule (CLAUDE.md §6) governing a weekly schedule as a whole, kept
// beside ResourceWriteRules for the same reason: one thrower per reason code.
//
// The per-window invariants are elsewhere and deliberately so — ClosesAt >
// OpensAt is tier 1 (CK_AvailabilityWindows_Window, restated in
// AvailabilityWindow's constructor), and field shape is the validator's. What
// is left here is the only rule that cannot be judged one window at a time.
internal static class AvailabilityWindowRules
{
    // Overlaps are rejected, not unioned (docs/wp3-plan.md's "smaller calls").
    // Unioning would silently return a schedule the admin did not send, and
    // "why does my resource open at 08:00 when I set 09:00" is a much worse bug
    // to find than a 409.
    //
    // Adjacency is not overlap: ClosesAt is exclusive, so 09:00-12:00 and
    // 12:00-17:00 coexist. Phase 5 may well merge them when it expands windows
    // into UTC intervals; that is its business, and not a reason to refuse the
    // admin's chosen shape here.
    public static void EnsureNoOverlaps(IReadOnlyList<AvailabilityWindowDefinition> windows)
    {
        foreach (var weekday in windows.GroupBy(w => w.Weekday))
        {
            var ordered = weekday.OrderBy(w => w.OpensAt).ToList();

            // Comparing each window against only its immediate predecessor is
            // sufficient once they are sorted by OpensAt, which is worth
            // spelling out because it looks like it would miss a long window
            // swallowing several short ones. It does not: if no adjacent pair
            // overlaps then ClosesAt is strictly increasing too (each window
            // starts at or after the previous one closes, and closes after it
            // starts), so a later window cannot reach back past its neighbour.
            // The first violation throws, so nothing after it is examined.
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].OpensAt < ordered[i - 1].ClosesAt)
                {
                    throw new OverlappingAvailabilityWindowException(
                        ordered[i].Weekday, ordered[i].OpensAt, ordered[i].ClosesAt);
                }
            }
        }
    }
}
