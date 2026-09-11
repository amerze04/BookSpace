namespace BookSpace.IntegrationTests.Support;

// Hardening pass — engineering/CI item. Every integration test file used to
// hardcode "Server=localhost\SQLEXPRESS;..." directly, which only ever ran
// against a local Windows SQL Server Express instance and could not run in
// GitHub Actions or any other CI environment. Centralized here so CI (a Linux
// SQL Server container, which needs SQL authentication rather than Windows
// Trusted_Connection) can override it with one environment variable instead
// of eighteen source edits.
//
// The default is exactly the connection string every test file already used,
// so a local `dotnet test` run with no environment variables set behaves
// identically to before this pass.
public static class IntegrationTestSettings
{
    private const string DefaultTemplate =
        "Server=localhost\\SQLEXPRESS;Database={0};Trusted_Connection=True;TrustServerCertificate=True;";

    // {0} is replaced with the specific test suite's throwaway database name
    // (BookSpace_AuthTests, BookSpace_SeedDataTests, etc. — each test host
    // creates and drops its own). See .github/workflows/backend-ci.yml for
    // the CI override, which points this at the SQL Server service container
    // with SQL authentication instead.
    public static string ConnectionStringFor(string databaseName)
    {
        var template = Environment.GetEnvironmentVariable("BOOKSPACE_TEST_CONNECTION_STRING_TEMPLATE")
            ?? DefaultTemplate;

        return string.Format(template, databaseName);
    }
}
