namespace BookSpace.Infrastructure.Email;

// Which IEmailSender the application resolves. Fixed at startup by
// DependencyInjection reading configuration once — changing it needs a restart,
// which is the same standing every other wiring decision in this application
// has.
public enum EmailDeliveryMode
{
    // Writes each message to disk as an .eml file and sends nothing. This is
    // how the invitation flow runs on a developer machine, where there is no
    // provider account — it is not a test double.
    DevelopmentSink = 0,

    Smtp = 1,
}

// How the SMTP connection is secured. An explicit three-value enum rather than
// a bool, because the three deployments are genuinely different and a bool
// cannot say which: 587 with STARTTLS (every transactional provider's relay),
// 465 with TLS from the first byte, and an unencrypted local relay.
public enum SmtpSecurity
{
    StartTls = 0,
    SslOnConnect = 1,
    None = 2,
}

// Bound from the "Email" configuration section and validated at startup
// (EmailOptionsValidator + ValidateOnStart in DependencyInjection).
//
// Smtp:Username and Smtp:Password are never in appsettings.json — user-secrets
// in development, Email__Smtp__Password elsewhere (CLAUDE.md §4.4), exactly as
// Jwt:SigningKey already works.
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    // Smtp, deliberately, so an omitted or misspelled section fails the boot
    // instead of quietly writing production invitations to a directory nobody
    // reads. A forgotten setting has to break loudly rather than swallow mail.
    public EmailDeliveryMode DeliveryMode { get; set; } = EmailDeliveryMode.Smtp;

    public string FromAddress { get; set; } = string.Empty;

    public string? FromDisplayName { get; set; }

    public SmtpEmailOptions Smtp { get; set; } = new();

    public DevelopmentSinkOptions DevelopmentSink { get; set; } = new();
}

public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? Username { get; set; }

    public string? Password { get; set; }

    // Bounded on purpose. docs/user-management-plan.md §4.1 accepts that the
    // create-user request now depends on a third party's latency; this is what
    // keeps that dependency from being unbounded. MailKit's own default is two
    // minutes, which is far longer than an admin will wait for a page.
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class DevelopmentSinkOptions
{
    // Relative paths resolve against the content root, so the default lands in
    // backend/src/BookSpace.Api/sent-emails when the API is run from its own
    // directory. Those files contain live activation links — the directory is
    // gitignored.
    public string Directory { get; set; } = "sent-emails";
}
