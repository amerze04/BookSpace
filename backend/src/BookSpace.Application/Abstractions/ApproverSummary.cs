namespace BookSpace.Application.Abstractions;

// A user, as an approver list needs to show them (FR-3.3). A port output, not an
// endpoint's contract — the same standing IssuedTokens has
// (docs/decisions/0015), so the "response DTOs are never shared" rule does not
// apply: each endpoint still maps this onto its own record.
//
// FullName and no email. A bare Guid is useless to a client, and the read detail
// is on the TenantMember policy — every member of the tenant sees this, not just
// the admin who set it. A name answers "who approves this room"; an email address
// is contact information nobody asked for.
public sealed record ApproverSummary(Guid UserId, string FullName);
