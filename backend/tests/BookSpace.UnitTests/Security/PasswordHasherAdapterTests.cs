using BookSpace.Infrastructure.Security;

namespace BookSpace.UnitTests.Security;

// FR-2.3. Thin wrapper, but two behaviors are ours rather than the library's:
// the plaintext must never be recoverable from the stored value, and a
// malformed stored hash must not throw out of the login path.
public class PasswordHasherAdapterTests
{
    [Fact]
    public void Hash_DoesNotContainThePlaintext()
    {
        var hasher = new PasswordHasherAdapter();

        var hash = hasher.Hash("Passw0rd!");

        Assert.DoesNotContain("Passw0rd!", hash);
    }

    [Fact]
    public void Hash_SaltsPerCall_SoIdenticalPasswordsHashDifferently()
    {
        var hasher = new PasswordHasherAdapter();

        Assert.NotEqual(hasher.Hash("Passw0rd!"), hasher.Hash("Passw0rd!"));
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash("Passw0rd!");

        Assert.True(hasher.Verify(hash, "Passw0rd!"));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash("Passw0rd!");

        Assert.False(hasher.Verify(hash, "wrong"));
    }

    // A row left over from the WP-1 placeholder seed would otherwise throw
    // FormatException out of PasswordHasher and turn a failed login into a 500.
    [Fact]
    public void Verify_MalformedStoredHash_ReturnsFalseInsteadOfThrowing()
    {
        var hasher = new PasswordHasherAdapter();

        Assert.False(hasher.Verify("seed-placeholder-hash", "Passw0rd!"));
    }
}
