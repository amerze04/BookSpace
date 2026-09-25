using BookSpace.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Security;

// Appends the raw token to the configured activation page as a `token` query
// parameter.
//
// Built with UriBuilder and Uri.EscapeDataString rather than string
// concatenation. That matters less than it usually would — SecureToken
// deliberately produces Base64Url, which has nothing in it that needs escaping
// — but the safety here is against the *other* half: an ActivationUrl that
// already carries a query string ("…/activate?lang=en") would silently produce
// a second '?' under concatenation, and the link would break in a way nobody
// tests for because the default configuration has no query string.
internal sealed class ActivationLinkBuilder : IActivationLinkBuilder
{
    private readonly string _activationUrl;

    public ActivationLinkBuilder(IOptions<ActivationOptions> options)
    {
        _activationUrl = options.Value.ActivationUrl;
    }

    public string BuildFor(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var builder = new UriBuilder(_activationUrl);
        var token = $"token={Uri.EscapeDataString(rawToken)}";

        // UriBuilder.Query round-trips with a leading '?', which has to be
        // stripped before appending or the result carries two.
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? token
            : $"{builder.Query.TrimStart('?')}&{token}";

        return builder.Uri.ToString();
    }
}
