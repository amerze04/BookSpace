namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation. A capacity decrease that would leave bookings
// already on the books over the new limit (wp3-plan's "smaller calls").
//
// The numbers are units, not bookings — docs/decisions/0005 makes Capacity a
// count of concurrent units, so the comparison is against the peak of
// overlapping Quantity at a single instant.
public sealed class CapacityBelowExistingBookingsException : AppException
{
    public CapacityBelowExistingBookingsException(int requestedCapacity, int peakConcurrentQuantity)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.CapacityBelowExistingBookings,
            $"Capacity {requestedCapacity} is below the {peakConcurrentQuantity} units already "
            + "committed at one instant by existing bookings.")
    {
    }
}
