using BookSpace.Application.Features.Users.CreateUser;

namespace BookSpace.UnitTests.Users;

// Shape only — whether the address is already taken is UQ_Users_Email's answer,
// not this validator's. The lengths matter because they are the column widths:
// a value this accepts but the database cannot store would be a 500 where a 400
// naming the field belongs.
public class CreateUserValidatorTests
{
    private readonly CreateUserCommandRequestValidator _validator = new();

    [Fact]
    public void AWellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Request()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingEmail_IsInvalid(string email)
    {
        Assert.False(_validator.Validate(Request(email: email)).IsValid);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("ada@")]
    [InlineData("@acme.test")]
    [InlineData("ada lovelace@acme.test")]
    public void AMalformedEmail_IsInvalid(string email)
    {
        Assert.False(_validator.Validate(Request(email: email)).IsValid);
    }

    // Users.Email is nvarchar(320).
    [Fact]
    public void AnEmailBeyondTheColumnWidth_IsInvalid()
    {
        var email = new string('a', 320 - "@acme.test".Length + 1) + "@acme.test";

        Assert.False(_validator.Validate(Request(email: email)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingFullName_IsInvalid(string fullName)
    {
        Assert.False(_validator.Validate(Request(fullName: fullName)).IsValid);
    }

    // Users.FullName is nvarchar(200).
    [Fact]
    public void AFullNameBeyondTheColumnWidth_IsInvalid()
    {
        Assert.False(_validator.Validate(Request(fullName: new string('n', 201))).IsValid);
    }

    [Fact]
    public void AFullNameExactlyAtTheColumnWidth_IsValid()
    {
        Assert.True(_validator.Validate(Request(fullName: new string('n', 200))).IsValid);
    }

    private static CreateUserCommandRequest Request(
        string email = "ada@acme.test",
        string fullName = "Ada Lovelace") => new(email, fullName);
}
