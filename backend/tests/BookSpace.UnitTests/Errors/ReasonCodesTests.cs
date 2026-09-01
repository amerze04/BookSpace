using System.Reflection;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Authentication;

namespace BookSpace.UnitTests.Errors;

// A reason code is the client's contract (CLAUDE.md §6), and the two ways it
// can quietly break are a value that doesn't match its member name and two
// codes sharing a value. Both are copy-paste mistakes that compile, pass every
// other test, and only surface as a client branching on a string that never
// arrives — so they get a test of their own.
public class ReasonCodesTests
{
    private static readonly Type[] CatalogueTypes = [typeof(ReasonCodes), typeof(AuthenticationFailureReason)];

    public static TheoryData<string, string, string> AllCodes()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var type in CatalogueTypes)
        {
            foreach (var field in Constants(type))
            {
                data.Add(type.Name, field.Name, (string)field.GetRawConstantValue()!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void EveryCodesValueMatchesItsName(string typeName, string fieldName, string value)
    {
        Assert.Equal(fieldName, value);
        Assert.False(string.IsNullOrWhiteSpace(typeName));
    }

    // Across both files, not just within one: the catalogue is split (see
    // ReasonCodes' header), and the split is only safe while the codes can't
    // collide.
    [Fact]
    public void CodesAreUniqueAcrossBothCatalogues()
    {
        var values = CatalogueTypes
            .SelectMany(Constants)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    // The six codes CLAUDE.md §6 names are the ones FR-4.5 committed to; four
    // are booking codes with no thrower until WP-4, which is exactly why they
    // are easy to lose track of.
    [Theory]
    [InlineData("SlotUnavailable")]
    [InlineData("CapacityExceeded")]
    [InlineData("OutsideAvailability")]
    [InlineData("BlackoutPeriod")]
    [InlineData("ResourceArchived")]
    [InlineData("ApprovalRequired")]
    public void SectionSixCodesAreAllPresent(string expectedCode)
    {
        var values = Constants(typeof(ReasonCodes))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Contains(expectedCode, values);
    }

    private static IEnumerable<FieldInfo> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string));
}
