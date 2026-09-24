using System.ComponentModel.DataAnnotations;

namespace BookSpace.Infrastructure.Security;

// Bound from the "Activation" configuration section and validated at startup
// (ValidateDataAnnotations + ValidateOnStart in DependencyInjection), the same
// way JwtOptions is.
//
// Data annotations here rather than an IValidateOptions class, unlike
// EmailOptions next door: every rule is unconditional, which is exactly the
// case annotations handle well. EmailOptions needed the other mechanism because
// half of its rules depend on the delivery mode.
//
// Nothing secret in this section, so unlike Jwt and Email it is committed whole.
public sealed class ActivationOptions
{
    public const string SectionName = "Activation";

    // How long an invitation stays redeemable.
    //
    // Seven days is a judgement call, not a requirement — nothing in the PRD
    // names one. It is long enough to survive a holiday weekend and short
    // enough that a link sitting in an unattended mailbox stops being a way in.
    // Absolute: nothing extends it, and there is no way to re-issue an
    // invitation today (docs/user-management-plan.md §6), which is the argument
    // for the upper end of this range rather than a shorter window.
    [Range(1, 90)]
    public int TokenLifetimeDays { get; set; } = 7;

    // Where an invitation link points: the frontend page that collects a
    // password and posts it to /auth/activate. The raw token is appended as a
    // `token` query parameter (ActivationLinkBuilder).
    //
    // **Required, with no default**, on the same reasoning Cors:AllowedOrigins
    // is empty by default and EmailOptions has no fallback host: the API cannot
    // guess its frontend's origin, and a guess would produce invitations that
    // look right and lead nowhere. Startup fails instead.
    //
    // A full URL rather than an origin plus a hard-coded path, so the frontend
    // can move the page without a backend change.
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string ActivationUrl { get; set; } = string.Empty;
}
