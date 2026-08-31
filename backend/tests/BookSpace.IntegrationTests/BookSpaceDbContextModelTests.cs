using BookSpace.IntegrationTests.Support;
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
        return new BookSpaceDbContext(options, new FixedCurrentTenant(null));
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

    // CLAUDE.md §4.3, the read side. datetime2 stores no offset, so EF
    // materializes every instant as DateTimeKind.Unspecified and
    // System.Text.Json then omits the trailing "Z" — a client parses it as local
    // time. The converter in OnModelCreating restores the Kind; this makes sure
    // a property added later gets it too, since the loop is easy to bypass with
    // an explicit HasConversion in a configuration.
    [Fact]
    public void Model_RestoresUtcKindOnEveryDateTimeProperty()
    {
        using var context = CreateContext();

        var offenders = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .Where(p => p.GetValueConverter() is null)
            .Select(p => $"{p.DeclaringType.ShortName()}.{p.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void UtcKindConverter_LeavesTheStoredValueUnchanged()
    {
        using var context = CreateContext();

        var property = context.Model.FindEntityType(typeof(BookSpace.Domain.Entities.Resource))!
            .FindProperty(nameof(BookSpace.Domain.Entities.Resource.CreatedAtUtc))!;
        var converter = property.GetValueConverter()!;

        var instant = new DateTime(2026, 8, 31, 13, 49, 35, DateTimeKind.Utc);

        // Writing is the identity — the value is already UTC (§4.3), and a
        // converter that shifted it would be rewriting data.
        Assert.Equal(instant, converter.ConvertToProvider(instant));
        // Reading only restores the Kind the column cannot carry.
        var read = Assert.IsType<DateTime>(converter.ConvertFromProvider(
            new DateTime(2026, 8, 31, 13, 49, 35, DateTimeKind.Unspecified)));
        Assert.Equal(instant, read);
        Assert.Equal(DateTimeKind.Utc, read.Kind);
    }
}
