namespace BookSpace.IntegrationTests.Authentication;

// One host (and one throwaway database) shared by the auth integration tests.
// Booting the API and running migrations per test class would multiply an
// already slow setup for no isolation benefit — the tests scope their
// assertions to the family or account they created instead.
[CollectionDefinition(nameof(AuthenticationTestCollection))]
public sealed class AuthenticationTestCollection : ICollectionFixture<AuthenticationTestHost>
{
}
