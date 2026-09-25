using BookSpace.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Security;

// The activation-token half of SecureToken's rule. Identical generation and
// hashing to RefreshTokenFactory by construction — they share the primitive —
// and a separate class only because the lifetime is a different policy read
// from a different configuration section.
internal sealed class ActivationTokenFactory : IActivationTokenFactory
{
    public ActivationTokenFactory(IOptions<ActivationOptions> options)
    {
        Lifetime = TimeSpan.FromDays(options.Value.TokenLifetimeDays);
    }

    public TimeSpan Lifetime { get; }

    public GeneratedActivationToken Create()
    {
        var (raw, hash) = SecureToken.Create();
        return new GeneratedActivationToken(raw, hash);
    }

    public string HashOf(string rawToken) => SecureToken.HashOf(rawToken);
}
