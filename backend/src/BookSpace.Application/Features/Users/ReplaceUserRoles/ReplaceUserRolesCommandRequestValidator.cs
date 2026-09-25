using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Features.Users.ReplaceUserRoles;

// Shape only. Whether the change is *allowed* — the last-admin guard — is the
// handler's job, because it needs a locking read this layer cannot do.
public sealed class ReplaceUserRolesCommandRequestValidator
    : AbstractValidator<ReplaceUserRolesCommandRequest>
{
    public ReplaceUserRolesCommandRequestValidator()
    {
        RuleFor(c => c.UserId)
            .NotEmpty()
            .WithMessage("UserId is required.");

        // **NotEmpty, unlike the approver list next door, which takes an empty
        // array as a real request.** The difference is what the empty state
        // means. A resource with no approvers is a coherent thing (decision
        // `0028` made it a supported one); a user with no roles is not — they
        // keep full member access regardless, because
        // AuthorizationPolicies.TenantMember requires only the orgId claim. So
        // an empty set would store a row that lies about what the account can
        // do, which is the same reason POST /users assigns Member rather than
        // nothing (phase 3). An admin who wants to take everything away
        // deactivates the account.
        RuleFor(c => c.Roles)
            .NotEmpty()
            .WithMessage("Roles is required and must contain at least one role; "
                + "deactivate the user instead of clearing their roles.");

        // Bound by name from the JSON body, so an unrecognized word never
        // reaches here. This catches the numeric form: Enum.TryParse accepts any
        // integer, and an undefined Role would be stored as a string
        // CK_UserRoles_Role then rejects — a 500 where a 400 naming the field
        // belongs.
        RuleForEach(c => c.Roles)
            .IsInEnum()
            .WithMessage($"Roles must contain only: {string.Join(", ", Enum.GetNames<Role>())}.");

        // **A privilege-escalation guard, not tidiness.** SysAdmin is the
        // platform operator (PRD §2, decision `0012`) and is deliberately not a
        // tenant role: a SysAdmin sits above every tenant and is excluded from
        // AuthorizationPolicies.TenantMember precisely so they never see tenant
        // content in routine operation. Nothing else stops this — the database's
        // CK_UserRoles_Role allows the value, because the bootstrap SysAdmin row
        // needs it — so without this rule a TenantAdmin could grant themselves
        // the platform role by editing a request body.
        //
        // A 400 rather than a reason code, because no legitimate client can send
        // it: the screen offers three checkboxes. The catalogue describes what
        // the API returns to callers acting in good faith.
        RuleFor(c => c.Roles)
            .Must(roles => roles is null || !roles.Contains(Role.SysAdmin))
            .WithMessage("SysAdmin is a platform role and cannot be assigned to a tenant user.");

        // Rejected rather than collapsed, exactly as ReplaceApprovers does: the
        // domain applies set semantics, so a repeated role would store cleanly
        // and the response would silently contain fewer entries than the request
        // (docs/decisions/0015 — never quietly alter what was submitted). It is
        // also a reliable sign of a client bug.
        RuleFor(c => c.Roles)
            .Must(roles => roles is null || roles.Distinct().Count() == roles.Count)
            .WithMessage("Roles must not contain duplicates.");

        // Hardening pass, 2026-09-25 (finding 6). Settles a model this codebase
        // had left half-stated: TenantMember access comes from the orgId claim
        // alone, not from holding Role.Member (the reason an empty set is
        // refused above) — but nothing stopped a set like [Approver] or
        // [TenantAdmin] from being *saved* without Member, leaving a row that
        // undersells what the account can actually do. The answer is that
        // every tenant user always carries Member; Approver and TenantAdmin are
        // additional, independent grants on top of it. A 400 rather than a
        // reason code, for the same reason as the SysAdmin rule above: the
        // screen renders Member as a mandatory, non-removable checkbox, so no
        // legitimate client can send a set missing it.
        RuleFor(c => c.Roles)
            .Must(roles => roles is null || roles.Contains(Role.Member))
            .WithMessage("Roles must include Member — every tenant user keeps member access "
                + "regardless of any other role, and the stored set should say so.");
    }
}
