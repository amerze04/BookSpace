using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;

namespace BookSpace.Api.Authorization;

// FR-1.4 RBAC. Named policies rather than [Authorize(Roles = "...")] at each
// call site, for the same reason CLAUDE.md §4.2 rejects a copy-pasted tenant
// filter: "TenantAdmin or SysAdmin" repeated across forty endpoints is forty
// chances to get it wrong. One definition here, referenced by name.
// See docs/decisions/0012-rbac-enforcement-model.md.
public static class AuthorizationPolicies
{
    public const string SysAdminOnly = nameof(SysAdminOnly);
    public const string TenantAdmin = nameof(TenantAdmin);
    public const string Approver = nameof(Approver);
    public const string TenantMember = nameof(TenantMember);

    public static void AddBookSpacePolicies(this AuthorizationOptions options)
    {
        // Platform operator. Deliberately not folded into the policies below.
        options.AddPolicy(SysAdminOnly, policy =>
            policy.RequireRole(nameof(Role.SysAdmin)));

        options.AddPolicy(TenantAdmin, policy =>
            policy.RequireRole(nameof(Role.TenantAdmin), nameof(Role.SysAdmin)));

        options.AddPolicy(Approver, policy =>
            policy.RequireRole(nameof(Role.Approver), nameof(Role.TenantAdmin), nameof(Role.SysAdmin)));

        // Any authenticated user who actually belongs to a tenant. SysAdmin is
        // excluded on purpose — PRD §2: the Platform Operator "must never see
        // tenant booking content in the course of routine operation", so
        // SysAdmin is a separate axis, not the top of a ladder. Requiring the
        // orgId claim expresses that directly: a SysAdmin has no such claim.
        options.AddPolicy(TenantMember, policy =>
            policy.RequireClaim(BookSpaceClaims.OrgId));

        // Secure by default: a controller added later is protected unless it
        // explicitly opts out with [AllowAnonymous]. Opt-in would leave every
        // new endpoint one forgotten attribute away from being public, which is
        // what "every endpoint enforces authorization server-side" rules out.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
}
