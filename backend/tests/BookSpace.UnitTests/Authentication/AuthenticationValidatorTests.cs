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
    public void LoginCommandRequest_InvalidInput_FailsValidation(string email, string password)
    {
        var result = new LoginCommandRequestValidator().Validate(new LoginCommandRequest(email, password));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void LoginCommandRequest_ValidInput_PassesValidation()
    {
        var result = new LoginCommandRequestValidator().Validate(new LoginCommandRequest("user@acme.test", "Passw0rd!"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LoginCommandRequest_ReportsTheOffendingFieldByName()
    {
        var result = new LoginCommandRequestValidator().Validate(new LoginCommandRequest("user@acme.test", ""));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LoginCommandRequest.Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefreshTokenCommandRequest_EmptyToken_FailsValidation(string token)
    {
        var result = new RefreshTokenCommandRequestValidator().Validate(new RefreshTokenCommandRequest(token));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RefreshTokenCommandRequest_ValidToken_PassesValidation()
    {
        var result = new RefreshTokenCommandRequestValidator().Validate(new RefreshTokenCommandRequest("some-token"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LogoutCommandRequest_EmptyToken_FailsValidation()
    {
        var result = new LogoutCommandRequestValidator().Validate(new LogoutCommandRequest(string.Empty));

        Assert.False(result.IsValid);
    }
}
