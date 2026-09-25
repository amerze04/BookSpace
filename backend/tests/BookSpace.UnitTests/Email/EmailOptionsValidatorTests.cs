using BookSpace.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace BookSpace.UnitTests.Email;

// This validator is what ValidateOnStart acts on, so it is the whole of the
// "a deployment that cannot send email fails the boot" guarantee — the same
// standing JwtOptionsTests has for the signing key (CLAUDE.md §4.4). A rule
// dropped here re-allows booting into a configuration that silently loses
// invitations, which is exactly the failure docs/user-management-plan.md §4.3
// is trying not to have.
public class EmailOptionsValidatorTests
{
    private readonly EmailOptionsValidator _validator = new();

    [Fact]
    public void FullyConfiguredSmtpOptions_AreValid()
    {
        Assert.True(Validate(ValidSmtp()).Succeeded);
    }

    [Fact]
    public void FullyConfiguredDevelopmentSinkOptions_AreValid()
    {
        Assert.True(Validate(ValidSink()).Succeeded);
    }

    // The safe direction, and the reason it is worth pinning: an omitted or
    // misspelled "Email" section binds to the defaults, and Smtp with no host
    // fails the boot. Had the default been DevelopmentSink, the same mistake
    // would have written production invitations to a directory nobody reads.
    [Fact]
    public void DefaultDeliveryMode_IsSmtp()
    {
        Assert.Equal(EmailDeliveryMode.Smtp, new EmailOptions().DeliveryMode);
    }

    [Fact]
    public void DefaultsMatchTheDocumentedTransportChoice()
    {
        var smtp = new SmtpEmailOptions();

        // 587 + STARTTLS is every transactional provider's relay.
        Assert.Equal(587, smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, smtp.Security);
        // Bounded, against MailKit's own two-minute default — §4.1's answer to
        // the create request now depending on a third party's latency.
        Assert.Equal(30, smtp.TimeoutSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingFromAddress_IsInvalid(string fromAddress)
    {
        var options = ValidSmtp();
        options.FromAddress = fromAddress;

        AssertFailsMentioning(options, "FromAddress");
    }

    // Measured against MimeKit 4.18 rather than assumed: TryParse *accepts*
    // "not-an-address" — a bare atom is a legal addr-spec for a local mailbox —
    // so the parser alone is not the rule. EmailAddressRules requires a domain
    // on top of it, which is what makes a typo in a deployment's From address
    // fail the boot rather than every send.
    [Theory]
    [InlineData("not-an-address")]
    [InlineData("a@")]
    [InlineData("@bookspace.example")]
    [InlineData("two addresses@bookspace.example")]
    public void AFromAddressMissingEitherHalf_IsInvalid(string fromAddress)
    {
        var options = ValidSmtp();
        options.FromAddress = fromAddress;

        AssertFailsMentioning(options, "FromAddress");
    }

    // Both modes need it: the sink writes a From header into the .eml it
    // produces, so an unusable one is just as wrong there.
    [Fact]
    public void MissingFromAddress_IsInvalidInSinkModeToo()
    {
        var options = ValidSink();
        options.FromAddress = string.Empty;

        AssertFailsMentioning(options, "FromAddress");
    }

    [Fact]
    public void SmtpModeWithoutHost_IsInvalid()
    {
        var options = ValidSmtp();
        options.Smtp.Host = string.Empty;

        AssertFailsMentioning(options, "Smtp:Host");
    }

    // The host is irrelevant to the sink, so requiring it would force a
    // developer to invent a provider they do not have.
    [Fact]
    public void SinkModeWithoutHost_IsValid()
    {
        var options = ValidSink();
        options.Smtp.Host = string.Empty;

        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void PortOutsideTheValidRange_IsInvalid(int port)
    {
        var options = ValidSmtp();
        options.Smtp.Port = port;

        AssertFailsMentioning(options, "Smtp:Port");
    }

    [Fact]
    public void NonPositiveTimeout_IsInvalid()
    {
        var options = ValidSmtp();
        options.Smtp.TimeoutSeconds = 0;

        AssertFailsMentioning(options, "Smtp:TimeoutSeconds");
    }

    // CLAUDE.md §4.4. AUTH over an unencrypted connection puts the provider's
    // credential on the wire in the clear on every single send, so this is
    // refused at startup rather than logged as a warning nobody reads.
    [Fact]
    public void UnencryptedTransportCarryingACredential_IsInvalid()
    {
        var options = ValidSmtp();
        options.Smtp.Security = SmtpSecurity.None;

        AssertFailsMentioning(options, "Security is None");
    }

    // An unauthenticated local relay is a real deployment and stays allowed —
    // it just cannot be handed a credential to leak.
    [Fact]
    public void UnencryptedTransportWithoutACredential_IsValid()
    {
        var options = ValidSmtp();
        options.Smtp.Security = SmtpSecurity.None;
        options.Smtp.Username = null;
        options.Smtp.Password = null;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void UsernameWithoutPassword_IsInvalid()
    {
        var options = ValidSmtp();
        options.Smtp.Password = null;

        AssertFailsMentioning(options, "must be supplied together");
    }

    [Fact]
    public void PasswordWithoutUsername_IsInvalid()
    {
        var options = ValidSmtp();
        options.Smtp.Username = null;

        AssertFailsMentioning(options, "must be supplied together");
    }

    [Fact]
    public void NeitherUsernameNorPassword_IsValid()
    {
        var options = ValidSmtp();
        options.Smtp.Username = null;
        options.Smtp.Password = null;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void SinkModeWithoutADirectory_IsInvalid()
    {
        var options = ValidSink();
        options.DevelopmentSink.Directory = string.Empty;

        AssertFailsMentioning(options, "DevelopmentSink:Directory");
    }

    // Every failure is collected rather than the first one thrown, so a
    // developer fixing their configuration sees the whole list at once.
    [Fact]
    public void SeveralProblems_AreAllReported()
    {
        var options = ValidSmtp();
        options.FromAddress = string.Empty;
        options.Smtp.Host = string.Empty;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("FromAddress"));
        Assert.Contains(result.Failures!, f => f.Contains("Smtp:Host"));
    }

    private ValidateOptionsResult Validate(EmailOptions options) =>
        _validator.Validate(Options.DefaultName, options);

    private void AssertFailsMentioning(EmailOptions options, string fragment)
    {
        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(fragment, StringComparison.Ordinal));
    }

    private static EmailOptions ValidSmtp() => new()
    {
        DeliveryMode = EmailDeliveryMode.Smtp,
        FromAddress = "no-reply@bookspace.example",
        FromDisplayName = "BookSpace",
        Smtp = new SmtpEmailOptions
        {
            Host = "smtp.provider.example",
            Port = 587,
            Security = SmtpSecurity.StartTls,
            Username = "apikey",
            Password = "s3cret",
        },
    };

    private static EmailOptions ValidSink() => new()
    {
        DeliveryMode = EmailDeliveryMode.DevelopmentSink,
        FromAddress = "no-reply@bookspace.example",
        DevelopmentSink = new DevelopmentSinkOptions { Directory = "sent-emails" },
    };
}
