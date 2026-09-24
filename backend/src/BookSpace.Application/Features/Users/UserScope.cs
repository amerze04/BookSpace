namespace BookSpace.Application.Features.Users;

// Which set of people GET /users answers with.
//
// Modelled on BookingScope, which already means "which rows" on GET /bookings,
// so the two list endpoints read the same way. Serialized by name — Program.cs
// registers JsonStringEnumConverter app-wide.
//
// **The default is the narrow one, and that is the whole design of this
// parameter** (docs/user-management-plan.md §4.6). GET /users existed before the
// directory did, answering the decision `0018` eligible-approver set for the
// approvers picker. Widening it risked the picker silently offering people
// ReplaceApprovers would then refuse — with `0018` collapsing every reason into
// ApproverNotEligible, so no screen could say why. Making the wider set opt-in
// inverts that: a forgotten parameter narrows rather than widens, which is the
// safe direction. Same shape as `includeArchived` on GET /resources.
public enum UserScope
{
    // Own-tenant, active, holding Approver or TenantAdmin (decision `0018`).
    // The default, so a caller that sends no scope gets exactly what it got
    // before this parameter existed.
    EligibleApprovers = 0,

    // Every user in the caller's tenant: Members too, and deactivated accounts,
    // which is what a directory has to show. Still tenant-scoped — that is the
    // query filter's job and no scope value can reach past it. A SysAdmin has no
    // OrgId and so appears in nobody's tenant.
    All = 1,
}
