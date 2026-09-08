namespace BookSpace.Application.Features.Bookings;

// How wide GET /bookings should look (WP-4 Phase 2a, owner's call 2026-09-08).
//
// FR-4.4 asks that a member "view and cancel their own bookings", so Own is the
// default and the only value a plain member may send. Tenant exists because an
// admin's real question is usually "what is booked next week", which the
// per-member userId filter can only answer one member at a time — and because
// WP-5's approver queue needs exactly this widening.
//
// An enum rather than a bool, so the default reads as a decision at the call
// site (`BookingScope.Own`) instead of as an absent flag, and so a third
// audience — an approver seeing the resources they gate, say — can be added
// without changing the parameter's meaning.
//
// Serialized and bound by name: JsonStringEnumConverter is registered app-wide
// (WP-3 Phase 3), and ASP.NET Core binds an enum query value with
// Enum.TryParse, so `?scope=tenant` works case-insensitively.
public enum BookingScope
{
    // Only the caller's own bookings. Every role may ask for this, including a
    // TenantAdmin, who is a member of their tenant before they are its admin.
    Own,

    // Every booking in the caller's tenant, whoever owns it. TenantAdmin only —
    // a plain member sending it gets ValidationFailed, not a quietly narrowed
    // answer (see ListBookingsQueryRequestValidator).
    Tenant,
}
