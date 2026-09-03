namespace BookSpace.Domain.Availability;

// A half-open span of real time, [StartUtc, EndUtc), and the unit every step of
// the availability calculation works in (WP-3 Phase 5).
//
// Half-open is what makes the rest of the algebra simple: two intervals that
// merely touch (one ends exactly where the next begins) describe one continuous
// span with no gap and no double-count, which is the same convention
// AvailabilityWindow.ClosesAt already uses — 09:00-12:00 and 12:00-17:00
// coexist because 12:00 belongs to the second window only.
//
// Kind is checked, not assumed. CLAUDE.md §4.3 has this system storing UTC in
// datetime2(0) columns that carry no offset, so a DateTime whose Kind was lost
// somewhere upstream looks identical to a correct one — which is exactly the
// bug the value converter in OnModelCreating exists to prevent. An interval is
// the boundary where local wall-clock time has already been resolved to
// instants, so it is the right place to insist.
//
// An empty span is not representable, deliberately: emptiness is expressed by
// an interval being absent from a list, so a caller never has to ask whether a
// returned interval is real. (default(UtcInterval) sidesteps the constructor,
// as it does for any struct; nothing in the algebra produces one.)
public readonly record struct UtcInterval
{
    public UtcInterval(DateTime startUtc, DateTime endUtc)
    {
        if (startUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("StartUtc must be a UTC instant.", nameof(startUtc));
        if (endUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("EndUtc must be a UTC instant.", nameof(endUtc));
        if (endUtc <= startUtc)
            throw new ArgumentException("EndUtc must be after StartUtc.", nameof(endUtc));

        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public DateTime StartUtc { get; }
    public DateTime EndUtc { get; }

    public TimeSpan Duration => EndUtc - StartUtc;
}
