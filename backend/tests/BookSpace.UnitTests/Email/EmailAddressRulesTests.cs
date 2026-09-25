using BookSpace.Infrastructure.Email;

namespace BookSpace.UnitTests.Email;

// One rule, used by the startup validator for the configured From address and
// by MimeMessageFactory for every recipient — so it is worth pinning on its
// own rather than only through those two.
//
// The interesting half is what MimeKit does *not* reject. These were measured
// against MimeKit 4.18 on 2026-09-23, not inferred from its documentation, and
// they are the reason this class exists at all.
public class EmailAddressRulesTests
{
    [Theory]
    [InlineData("no-reply@bookspace.example")]
    [InlineData("ada@acme.example")]
    [InlineData("ada+bookspace@acme.example")]
    [InlineData("a/b@acme.example")]
    // A single-label domain is legal and does occur on internal relays.
    [InlineData("a@b")]
    public void AUsableAddress_IsAccepted(string address)
    {
        Assert.True(EmailAddressRules.IsUsable(address));
    }

    // MimeKit's MailboxAddress constructor accepts an empty address and
    // produces a message with an empty To header. That message would be written
    // to the development sink and reported as delivered, which is the failure
    // this rule exists to make impossible.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAddress_IsRejected(string? address)
    {
        Assert.False(EmailAddressRules.IsUsable(address));
    }

    // MailboxAddress.TryParse returns true for this one: a bare atom is a legal
    // addr-spec for a local mailbox. On a transactional provider it is a typo,
    // so the parser alone is not the rule.
    [Fact]
    public void ABareAtomWithNoDomain_IsRejected()
    {
        Assert.True(MimeKit.MailboxAddress.TryParse("not-an-address", out _));
        Assert.False(EmailAddressRules.IsUsable("not-an-address"));
    }

    [Theory]
    [InlineData("a@")]
    [InlineData("@acme.example")]
    [InlineData("two addresses@acme.example")]
    [InlineData("@")]
    public void AnAddressMissingEitherHalf_IsRejected(string address)
    {
        Assert.False(EmailAddressRules.IsUsable(address));
    }
}
