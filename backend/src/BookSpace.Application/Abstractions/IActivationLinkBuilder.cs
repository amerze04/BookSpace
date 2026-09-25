namespace BookSpace.Application.Abstractions;

// Turns a raw activation token into the URL a recipient clicks.
//
// A port rather than a string the handler concatenates, because the base
// address is deployment configuration — the frontend's origin, which the API
// does not otherwise know — and because it is validated at startup alongside
// everything else in the Activation section. A handler building the URL itself
// would need that configuration in the Application layer, which CLAUDE.md §3
// keeps free of it.
//
// Small on purpose: it has one job and no state, so the interesting question —
// "what does the link actually look like?" — is answered in one file rather
// than inside a handler that is about something else.
public interface IActivationLinkBuilder
{
    // The raw token, never its hash. This is the one moment the plaintext
    // exists outside the recipient's mailbox, and the create response carries
    // it too (docs/user-management-plan.md §4.3) — so a caller holding this
    // string is holding a credential.
    string BuildFor(string rawToken);
}
