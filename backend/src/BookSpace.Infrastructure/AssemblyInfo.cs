using System.Runtime.CompilerServices;

// Same convention as BookSpace.Application: the security services
// (JwtAccessTokenService, RefreshTokenFactory, PasswordHasherAdapter) are
// internal because nothing outside Infrastructure should construct them, but
// their behavior — claim shape, hash determinism — is exactly what needs
// direct unit testing.
[assembly: InternalsVisibleTo("BookSpace.UnitTests")]
[assembly: InternalsVisibleTo("BookSpace.IntegrationTests")]
