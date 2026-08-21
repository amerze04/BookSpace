namespace BookSpace.Domain.Enums;

// Note: Rejected, not Declined — must match the PRD and DB CHECK exactly.
public enum BookingStatus
{
    Pending,
    Confirmed,
    Rejected,
    Cancelled,
    Completed,
    NoShow
}
