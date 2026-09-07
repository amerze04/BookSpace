namespace BookSpace.Application.Common.Errors;

// ErrorKind.NotFound (FR-4.4). No such booking visible to this caller.
//
// One code for three cases, deliberately, following BlackoutPeriodNotFound's
// reasoning one step further: the id exists nowhere, it belongs to another
// tenant, or it belongs to another member of this tenant and the caller is not
// a TenantAdmin. The first two are indistinguishable after CLAUDE.md §4.2's
// filters and must stay that way (AC-4).
//
// The third is the new one, and it is the reason this is a 404 rather than a
// 403. A member asking for a colleague's booking id is told the same thing as a
// member asking for a fabricated one — anything else confirms the booking
// exists and leaks who is holding which resource, which is the same disclosure
// AC-4 rules out across tenants, applied within one.
public sealed class BookingNotFoundException : AppException
{
    public BookingNotFoundException(Guid bookingId)
        : base(
            ErrorKind.NotFound,
            ReasonCodes.BookingNotFound,
            $"Booking {bookingId} was not found for the current caller.")
    {
    }
}
