using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.ListUsers;

// One row of GET /users: a person an admin may assign as an approver of a
// resource (FR-3.3).
//
// **Email is on the wire here and deliberately is not on ApproverSummary.** The
// difference is who is reading. ApproverSummary answers "who approves this
// room" for every member of the tenant, where an address is contact information
// nobody asked for; this endpoint is TenantAdmin-only and its job is picking one
// person out of a list, where two people called "A. Novak" are otherwise
// indistinguishable. The admin provisioned these accounts in the first place.
//
// Roles are included because decision `0018` gives the UI no other way to say
// anything true about eligibility. The picker cannot explain a refusal — every
// reason collapses to ApproverNotEligible — so showing what makes each person
// eligible is the compensating information, and it is already loaded.
//
// Serialized by name ("Approver", not 2) — Program.cs registers
// JsonStringEnumConverter app-wide.
//
// What is not here: IsActive (every row is active by construction — an inactive
// user is not eligible and never comes back), OrgId (the token's, or the row
// would not be visible), and anything from the audit columns.
public sealed record ListUsersQueryResponse(
    Guid Id,
    string FullName,
    string Email,
    IReadOnlyList<Role> Roles);
