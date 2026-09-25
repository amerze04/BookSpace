using MimeKit;

namespace BookSpace.Infrastructure.Email;

// What counts as an address this application will send to or from, in one
// place, so the configured From and a supplied recipient are held to the same
// standard.
//
// MimeKit's own parser is not that standard, and the difference was measured
// rather than assumed (2026-09-23, against MimeKit 4.18):
//
//   MailboxAddress.TryParse("not-an-address")  -> true, address "not-an-address"
//   new MailboxAddress("N", "")                -> succeeds, with an empty address
//
// Both are defensible for a library that has to read whatever arrives in a real
// mailbox — a bare atom is a legal addr-spec for a local mailbox — and both are
// wrong for a transactional provider on the public internet, where they are a
// typo and a bug respectively. The empty case is the dangerous one: it produces
// a message with an empty To that the sink would happily write to disk and call
// delivered.
internal static class EmailAddressRules
{
    public static bool IsUsable(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !MailboxAddress.TryParse(address, out var mailbox))
        {
            return false;
        }

        // A domain is required, and neither half may be empty. MimeKit has
        // already rejected "a@" and "@b" by this point; this is what rejects
        // the bare atom it accepts.
        var at = mailbox.Address.LastIndexOf('@');

        return at > 0 && at < mailbox.Address.Length - 1;
    }
}
