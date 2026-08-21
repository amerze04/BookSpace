using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.IntegrationTests;

// Building the model doesn't require a live connection — EF only needs one
// once a query actually runs — so this exercises every IEntityTypeConfiguration
// (check constraints, FK names, filtered/included indexes) purely as a
// regression guard against typos, duplicate constraint names, or fluent-API
// mistakes, without needing SQL Server up.
public class BookSpaceDbContextModelTests
{
    private static BookSpaceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer("Server=(local);Database=BookSpace_ModelBuildProbe;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new BookSpaceDbContext(options);
    }

    [Fact]
    public void OnModelCreating_BuildsWithoutThrowing()
    {
        using var context = CreateContext();

        var model = context.Model;

        Assert.NotEmpty(model.GetEntityTypes());
    }

    [Theory]
    [InlineData("Organizations")]
    [InlineData("Users")]
    [InlineData("UserRoles")]
    [InlineData("Resources")]
    [InlineData("ResourceApprovers")]
    [InlineData("AvailabilityWindows")]
    [InlineData("BlackoutPeriods")]
    [InlineData("RecurrenceRules")]
    [InlineData("Bookings")]
    [InlineData("ApprovalRequests")]
    [InlineData("Notifications")]
    [InlineData("RefreshTokens")]
    public void Model_MapsExpectedTable(string tableName)
    {
        using var context = CreateContext();

        var mappedTableNames = context.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .ToList();

        Assert.Contains(tableName, mappedTableNames);
    }

    [Fact]
    public void Model_UsesZeroPrecisionForEveryDateTimeAndTimeOnlyProperty()
    {
        using var context = CreateContext();

        var offenders = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)
                        || p.ClrType == typeof(TimeOnly) || p.ClrType == typeof(TimeOnly?))
            .Where(p => p.GetPrecision() != 0)
            .ToList();

        Assert.Empty(offenders);
    }
}
