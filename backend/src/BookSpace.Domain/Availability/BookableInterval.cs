namespace BookSpace.Domain.Availability;

// The availability calculation's answer: a span of time, and how many units of
// the resource are still free for the whole of it.
//
// This is WP-3 decision D2 in one type. A slot is not bookable-or-not, because
// Capacity counts concurrent units (docs/decisions/0005-capacity-semantics.md) —
// so the answer to "what can I book" is an interval with a number, never a
// boolean, and never a fixed grid of half-hour slots the PRD does not ask for.
//
// RemainingCapacity is guaranteed greater than zero, which is what makes the
// type's name true: an interval with nothing left is not a bookable interval and
// never reaches a caller. It is a *floor* across the whole span, not an average —
// the span is cut wherever the figure changes, so a client can trust the number
// at every instant inside it.
public readonly record struct BookableInterval
{
    public BookableInterval(UtcInterval interval, int remainingCapacity)
    {
        if (remainingCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingCapacity),
                "A bookable interval must have capacity remaining.");
        }

        Interval = interval;
        RemainingCapacity = remainingCapacity;
    }

    public UtcInterval Interval { get; }
    public int RemainingCapacity { get; }
}
