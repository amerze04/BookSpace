namespace BookSpace.Domain.Availability;

// Subtracts existing bookings from open time, in units rather than in time.
//
// The difference from IntervalAlgebra.Subtract matters. A blackout removes
// instants: the resource is shut, and what it covers is simply gone. A booking
// removes *units*: Capacity counts concurrent units
// (docs/decisions/0005-capacity-semantics.md), so a booking of 1 unit against a
// capacity of 4 leaves the same time still open, with 3 units left. Only when
// the units run out does time disappear.
//
// So the output is not intervals but intervals-with-a-number, which is WP-3
// decision D2, and the span has to be cut wherever that number changes — a
// figure quoted across a span a client cannot trust at every instant inside it
// would be worse than no figure at all.
//
// This is the same arithmetic as IResourceRepository.PeakConcurrentBookedQuantity
// Async, which Phase 2 uses to refuse a capacity decrease that would strand
// existing bookings. That one reduces the whole range to a single worst case;
// this one reports every step of it. Written twice on purpose — the peak is one
// SQL aggregate over rows this code would otherwise have to load.
public static class CapacitySweep
{
    // requiredQuantity is how many units the caller wants to hold at once. It is
    // what makes the answer a straight one: "how long can I book" has no single
    // answer on a pooled resource, because the longest run where 1 unit is free
    // is longer than the one where 4 are. Given the number, the runs are
    // determined — see AvailabilityCalculator for the endpoint's default.
    public static IReadOnlyList<BookableInterval> Subtract(
        IReadOnlyList<UtcInterval> openIntervals,
        IEnumerable<BookedQuantity> bookings,
        int capacity,
        int requiredQuantity)
    {
        ArgumentNullException.ThrowIfNull(openIntervals);
        ArgumentNullException.ThrowIfNull(bookings);

        if (capacity <= 0) // CK_Resources_Capacity
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

        if (requiredQuantity <= 0) // CK_Bookings_Quantity
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredQuantity), "The required quantity must be greater than zero.");
        }

        // A sweep line. Each booking becomes two events — units claimed at its
        // start, released at its end — and walking them in order keeps a running
        // total of units held at the instant reached, without ever asking "which
        // bookings overlap this moment", which is the quadratic way to do it and
        // the one the PRD's only performance NFR ("responsive with hundreds of
        // bookings per resource") would eventually notice.
        var events = new List<(DateTime Instant, int Delta)>();

        foreach (var booking in bookings)
        {
            events.Add((booking.Interval.StartUtc, booking.Quantity));
            events.Add((booking.Interval.EndUtc, -booking.Quantity));
        }

        // By instant only, with no tie-break on the sign. It needs none: every
        // event at one instant is applied together, after the segment ending
        // there has been measured, so a booking ending exactly as another starts
        // cannot produce a spurious one-tick dip or spike whichever order they
        // land in.
        events.Sort((left, right) => left.Instant.CompareTo(right.Instant));

        var bookable = new List<BookableInterval>();

        // Carried across open intervals rather than recomputed inside each one:
        // openIntervals is ordered and non-overlapping (both Merge and Subtract
        // guarantee it), so one pass through the events serves all of them. This
        // is also what makes a booking that started before the range count
        // correctly — its claim is applied while catching up, long before the
        // first open interval is reached.
        var eventIndex = 0;
        var unitsHeld = 0;

        foreach (var open in openIntervals)
        {
            // Events at exactly the interval's start belong inside it: intervals
            // are half-open, so a booking beginning at that instant holds units
            // within it, and one ending at that instant no longer does. The same
            // `<=` gets both right.
            while (eventIndex < events.Count && events[eventIndex].Instant <= open.StartUtc)
            {
                unitsHeld += events[eventIndex].Delta;
                eventIndex++;
            }

            var segments = new List<BookableSegment>();
            var segmentStart = open.StartUtc;

            while (eventIndex < events.Count && events[eventIndex].Instant < open.EndUtc)
            {
                var boundary = events[eventIndex].Instant;

                if (boundary > segmentStart)
                {
                    segments.Add(new BookableSegment(segmentStart, boundary, capacity - unitsHeld));
                }

                // Every event at this instant, before measuring the next segment:
                // two bookings starting together are one boundary, not two.
                while (eventIndex < events.Count && events[eventIndex].Instant == boundary)
                {
                    unitsHeld += events[eventIndex].Delta;
                    eventIndex++;
                }

                segmentStart = boundary;
            }

            if (segmentStart < open.EndUtc)
            {
                segments.Add(new BookableSegment(segmentStart, open.EndUtc, capacity - unitsHeld));
            }

            AppendBookable(segments, requiredQuantity, bookable);
        }

        return bookable;
    }

    // Turns the sweep's segments into the runs a booker can actually take, and
    // the order of the two steps is load-bearing.
    //
    // **Drop what does not have room first.** A segment with fewer than
    // requiredQuantity units left cannot hold this booking at all, so it is a
    // wall — not a smaller opportunity.
    //
    // **Then rejoin what is left, wherever it touches.** Every remaining segment
    // can hold the booking, so a boundary between two of them is not a boundary
    // for *this* caller: it is where the figure changed, which the caller did not
    // ask about. Rejoining is what makes the answer usable — otherwise a member
    // wanting one unit is shown a free day chopped up at every booking someone
    // else made, and has to work out for themselves that the pieces join.
    //
    // Doing it the other way round would rejoin across a wall and report time
    // that cannot hold the booking. And rejoining stays confined to one open
    // interval — segments either side of a closing time or a blackout are not
    // adjacent, whatever their capacity says.
    //
    // The reported figure is the **minimum** across a rejoined run, which keeps
    // BookableInterval's promise: the number holds at every instant inside the
    // span, so any sub-span of it can be booked at that quantity. The cost is
    // that a run hides the fact that part of it had *more* free than the rest; a
    // caller who cares asks again with a higher requiredQuantity, which is what
    // that parameter is for.
    private static void AppendBookable(
        List<BookableSegment> segments,
        int requiredQuantity,
        List<BookableInterval> bookable)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            // Not enough room — including zero, and including a negative, which
            // dbo.CreateBooking's capacity check makes unreachable (CLAUDE.md
            // §4.1) but which this code cannot verify, so it is treated as a wall
            // rather than trusted.
            if (segments[i].RemainingCapacity < requiredQuantity)
            {
                continue;
            }

            var segment = segments[i];
            var floor = segment.RemainingCapacity;

            while (i + 1 < segments.Count && segments[i + 1].RemainingCapacity >= requiredQuantity)
            {
                floor = Math.Min(floor, segments[i + 1].RemainingCapacity);
                segment = segment with { EndUtc = segments[i + 1].EndUtc };
                i++;
            }

            bookable.Add(new BookableInterval(new UtcInterval(segment.StartUtc, segment.EndUtc), floor));
        }
    }

    // Internal to the sweep because it is allowed to be uninteresting: a
    // remaining capacity of zero or less is a normal intermediate result here,
    // whereas BookableInterval refuses one by design. Nothing outside this class
    // should be able to hold a "bookable" span with nothing left in it.
    private readonly record struct BookableSegment(DateTime StartUtc, DateTime EndUtc, int RemainingCapacity);
}
