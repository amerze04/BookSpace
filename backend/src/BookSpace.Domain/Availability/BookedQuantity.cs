namespace BookSpace.Domain.Availability;

// One live booking's claim on a resource, reduced to the only two facts the
// availability calculation needs: when it runs, and how many units it holds.
//
// Quantity, not a boolean: Capacity counts *concurrent units*, so a booking does
// not make a slot unavailable, it consumes part of it
// (docs/decisions/0005-capacity-semantics.md).
//
// Deliberately not the Booking entity. The calculation has no use for a title, a
// status or an owner, and a Domain function that took the aggregate would force
// its caller — and WP-4's rejection checks after it — to load whole bookings
// where a three-column projection does. Filtering to the bookings that count
// (Pending and Confirmed) belongs to whoever builds this list, since that is a
// query, not arithmetic.
public readonly record struct BookedQuantity
{
    public BookedQuantity(UtcInterval interval, int quantity)
    {
        if (quantity <= 0) // CK_Bookings_Quantity
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");

        Interval = interval;
        Quantity = quantity;
    }

    public UtcInterval Interval { get; }
    public int Quantity { get; }
}
