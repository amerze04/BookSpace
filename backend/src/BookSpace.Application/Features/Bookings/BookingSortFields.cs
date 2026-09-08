namespace BookSpace.Application.Features.Bookings;

// The `sort` whitelist for GET /bookings (docs/decisions/0015). Shared by
// ListBookingsQueryRequestValidator, which rejects anything not here, and the
// repository, which maps a canonical name onto a typed OrderBy — neither
// invents its own list.
//
// Spelled the way each field appears in the response JSON, so `sort=-startsAtUtc`
// names something the client can actually see.
//
// StartsAtUtc is the default: a booking list is read as a schedule, so
// chronological order needs no explanation — the same reasoning as the blackout
// list. CreatedAtUtc is the audit order ("what was booked most recently"), which
// is a different question and not derivable from the first. Status earns its
// place from the admin scope: `?scope=tenant&sort=status` groups the Pending
// ones an admin is looking for, and it orders by the enum's stored name.
//
// Deliberately short, per decision 0015: every entry is API surface that has to
// keep working. Quantity and Title are omitted because nobody browses a schedule
// by either, and UserId because sorting by an opaque id orders nothing a reader
// can see.
public static class BookingSortFields
{
    public const string StartsAtUtc = "startsAtUtc";
    public const string CreatedAtUtc = "createdAtUtc";
    public const string Status = "status";

    public static readonly IReadOnlyCollection<string> All = [StartsAtUtc, CreatedAtUtc, Status];
}
