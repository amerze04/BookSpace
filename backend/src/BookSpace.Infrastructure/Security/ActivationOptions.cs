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
}
