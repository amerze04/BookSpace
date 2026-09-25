using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Email;

// Every rule about what a usable email configuration is, in one place.
//
// JwtOptions next door uses data annotations, and this deliberately does not:
// half of these rules depend on DeliveryMode, which an attribute cannot express,
// and data annotations do not recurse into the nested Smtp/DevelopmentSink
// objects at all. Splitting the rules across two mechanisms would mean a reader
// asking "is this configuration valid?" has to check both and know which covers
// what. ValidateOnStart runs this the same way it runs JwtOptions' attributes,
// so the guarantee is unchanged: a deployment that cannot send email fails the
// boot rather than dropping invitations silently.
internal sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var failures = new List<string>();

        // Needed by both modes: the sink writes the From header into the .eml
        // it produces, so a missing one is just as wrong there.
        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            failures.Add($"{EmailOptions.SectionName}:FromAddress is required.");
        }
        else if (!EmailAddressRules.IsUsable(options.FromAddress))
        {
            failures.Add(
                $"{EmailOptions.SectionName}:FromAddress ('{options.FromAddress}') is not a valid email address.");
        }

        switch (options.DeliveryMode)
        {
            case EmailDeliveryMode.Smtp:
                ValidateSmtp(options.Smtp, failures);
                break;

            case EmailDeliveryMode.DevelopmentSink:
                if (string.IsNullOrWhiteSpace(options.DevelopmentSink.Directory))
                {
                    failures.Add(
                        $"{EmailOptions.SectionName}:DevelopmentSink:Directory is required when DeliveryMode is DevelopmentSink.");
                }

                break;

            default:
                failures.Add(
                    $"{EmailOptions.SectionName}:DeliveryMode '{options.DeliveryMode}' is not a supported delivery mode.");
                break;
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSmtp(SmtpEmailOptions smtp, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            failures.Add(
                $"{EmailOptions.SectionName}:Smtp:Host is required when DeliveryMode is Smtp.");
        }

        if (smtp.Port is < 1 or > 65535)
        {
            failures.Add($"{EmailOptions.SectionName}:Smtp:Port must be between 1 and 65535.");
        }

        if (smtp.TimeoutSeconds < 1)
        {
            failures.Add($"{EmailOptions.SectionName}:Smtp:TimeoutSeconds must be at least 1.");
        }

        var hasUsername = !string.IsNullOrWhiteSpace(smtp.Username);
        var hasPassword = !string.IsNullOrWhiteSpace(smtp.Password);

        // Refused rather than warned about: AUTH over an unencrypted connection
        // puts the provider's credential on the wire in the clear, which is a
        // CLAUDE.md §4.4 violation the application would otherwise commit
        // silently on every send. An unencrypted local relay is still allowed —
        // it just cannot be given a credential to leak.
        if (smtp.Security is SmtpSecurity.None && (hasUsername || hasPassword))
        {
            failures.Add(
                $"{EmailOptions.SectionName}:Smtp:Security is None, which would send the SMTP credential unencrypted. Use StartTls or SslOnConnect, or remove Username/Password.");
        }

        // Either both or neither. One without the other is always a
        // misconfiguration, and the symptom — an anonymous connection the
        // provider rejects — does not point at the cause.
        if (hasUsername != hasPassword)
        {
            failures.Add(
                $"{EmailOptions.SectionName}:Smtp:Username and {EmailOptions.SectionName}:Smtp:Password must be supplied together.");
        }
    }
}
