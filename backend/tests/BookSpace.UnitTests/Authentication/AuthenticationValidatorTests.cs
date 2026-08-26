using BookSpace.Application.Features.Authentication.Login;
using BookSpace.Application.Features.Authentication.Logout;
using BookSpace.Application.Features.Authentication.Refresh;

namespace BookSpace.UnitTests.Authentication;

// These validators are what the ValidationBehavior short-circuits on, turning
// into a 400 with field errors before the handler runs. Worth testing directly:
// per CLAUDE.md §12 a mistyped or missing validator is a silent pass-through,
// so the only proof one is wired correctly is asserting it rejects.
public class AuthenticationValidatorTests
{
    [Theory]
    [InlineData("", "Passw0rd!")]
    [InlineData("   ", "Passw0rd!")]
    [InlineData("not-an-email", "Passw0rd!")]
    [InlineData("user@acme.test", "")]
    public void LoginCommand_InvalidInput_FailsValidation(string email, string password)
    {
        var result = new LoginCommandValidator().Validate(new LoginCommand(email, password));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void LoginCommand_ValidInput_PassesValidation()
    {
        var result = new LoginCommandValidator().Validate(new LoginCommand("user@acme.test", "Passw0rd!"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LoginCommand_ReportsTheOffendingFieldByName()
    {
        var result = new LoginCommandValidator().Validate(new LoginCommand("user@acme.test", ""));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LoginCommand.Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefreshTokenCommand_EmptyToken_FailsValidation(string token)
    {
        var result = new RefreshTokenCommandValidator().Validate(new RefreshTokenCommand(token));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RefreshTokenCommand_ValidToken_PassesValidation()
    {
        var result = new RefreshTokenCommandValidator().Validate(new RefreshTokenCommand("some-token"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LogoutCommand_EmptyToken_FailsValidation()
    {
        var result = new LogoutCommandValidator().Validate(new LogoutCommand(string.Empty));

        Assert.False(result.IsValid);
    }
}
