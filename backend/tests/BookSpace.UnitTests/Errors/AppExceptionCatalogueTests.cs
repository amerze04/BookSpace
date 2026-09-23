using System.Reflection;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Authentication;

namespace BookSpace.UnitTests.Errors;

// The executable version of what ReasonCodes could previously only say in a
// comment (2026-09-01, on the mentor's advice — see the amendment section of
// docs/decisions/0016-error-contract-and-reason-codes.md).
//
// Before this refactor, AppException took the kind and the reason code as two
// independent parameters, so `NotFound` alongside `ResourceArchived` compiled
// cleanly and shipped a 404 for something that should be a 422. The catalogue
// recorded the correct pairing beside each code, in prose. Now each failure is a
// named subclass that fixes both, and this test is what keeps the set of
// subclasses honest:
//
//   - every one carries a code that actually exists in the catalogue
//   - every one is paired with the kind the catalogue documents for that code
//   - a new subclass cannot be added without appearing in the table below
//
// It works by discovery, not by a hand-kept list of types, so the third point
// holds automatically: an exception class nobody added here fails the first test.
public class AppExceptionCatalogueTests
{
    // The kinds CLAUDE.md §6 and ReasonCodes document, in code. Adding a failure
    // means adding a row — which is the point: the pairing becomes a decision
    // someone has to write down, not a constructor argument they can fat-finger.
    private static readonly Dictionary<Type, (string ReasonCode, ErrorKind Kind)> Expected = new()
    {
        [typeof(ResourceNotFoundException)] = (ReasonCodes.ResourceNotFound, ErrorKind.NotFound),
        [typeof(ResourceArchivedException)] = (ReasonCodes.ResourceArchived, ErrorKind.RuleViolation),
        [typeof(InvalidTimeZoneIdException)] = (ReasonCodes.InvalidTimeZone, ErrorKind.Validation),
        [typeof(CapacityBelowExistingBookingsException)] =
            (ReasonCodes.CapacityBelowExistingBookings, ErrorKind.RuleViolation),
        [typeof(OverlappingAvailabilityWindowException)] =
            (ReasonCodes.OverlappingAvailabilityWindow, ErrorKind.Conflict),
        [typeof(ApproverNotEligibleException)] =
            (ReasonCodes.ApproverNotEligible, ErrorKind.RuleViolation),
        [typeof(BlackoutPeriodElapsedException)] =
            (ReasonCodes.BlackoutPeriodElapsed, ErrorKind.RuleViolation),
        [typeof(BlackoutPeriodNotFoundException)] =
            (ReasonCodes.BlackoutPeriodNotFound, ErrorKind.NotFound),

        // WP-4 Phase 1a — the four codes WP-4 adds.
        [typeof(BookingNotFoundException)] = (ReasonCodes.BookingNotFound, ErrorKind.NotFound),
        [typeof(BookingNotCancellableException)] =
            (ReasonCodes.BookingNotCancellable, ErrorKind.RuleViolation),
        [typeof(BookingDurationOutOfRangeException)] =
            (ReasonCodes.BookingDurationOutOfRange, ErrorKind.RuleViolation),
        [typeof(BookingInThePastException)] = (ReasonCodes.BookingInThePast, ErrorKind.RuleViolation),

        // WP-4 Phase 1c — the four §6 declared in WP-3 and left without a
        // thrower until the create path existed. The two Conflicts are the ones
        // to look at: a slot being taken is not a rule violation, it is a fact
        // about what else exists, and it may succeed on a retry.
        [typeof(OutsideAvailabilityException)] =
            (ReasonCodes.OutsideAvailability, ErrorKind.RuleViolation),
        [typeof(BookingInBlackoutPeriodException)] =
            (ReasonCodes.BlackoutPeriod, ErrorKind.RuleViolation),
        [typeof(SlotUnavailableException)] = (ReasonCodes.SlotUnavailable, ErrorKind.Conflict),
        [typeof(CapacityExceededException)] = (ReasonCodes.CapacityExceeded, ErrorKind.Conflict),

        // WP-5 Phase 1b.
        [typeof(NoOccurrencesCreatedException)] =
            (ReasonCodes.NoOccurrencesCreated, ErrorKind.RuleViolation),

        // WP-5 Phase 2.
        [typeof(RecurrenceRuleNotFoundException)] =
            (ReasonCodes.RecurrenceRuleNotFound, ErrorKind.NotFound),
        [typeof(RecurrenceRuleNotCancellableException)] =
            (ReasonCodes.RecurrenceRuleNotCancellable, ErrorKind.RuleViolation),

        // WP-5 Phase 3.
        [typeof(BookingNotPendingException)] =
            (ReasonCodes.BookingNotPending, ErrorKind.RuleViolation),
    };

    // AuthenticationException is excluded deliberately, and it is the one
    // legitimate exception to "one class per failure": it carries five different
    // codes on purpose, because every credential failure has to look identical
    // to the client (FR-2.1). Its codes are covered by the authentication handler
    // tests instead.
    private static IEnumerable<Type> DiscoveredSubclasses() =>
        typeof(AppException).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(AppException).IsAssignableFrom(t))
            .Where(t => t != typeof(AuthenticationException));

    public static TheoryData<Type> AllSubclasses()
    {
        var data = new TheoryData<Type>();
        foreach (var type in DiscoveredSubclasses())
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void EverySubclassIsAccountedForInTheTable()
    {
        var undocumented = DiscoveredSubclasses().Where(t => !Expected.ContainsKey(t)).ToList();

        Assert.Empty(undocumented);
    }

    // The table must not outlive its types either — a row for a class that was
    // renamed or deleted is a stale claim about the contract.
    [Fact]
    public void EveryTableRowHasALiveType()
    {
        var discovered = DiscoveredSubclasses().ToHashSet();
        var orphaned = Expected.Keys.Where(t => !discovered.Contains(t)).ToList();

        Assert.Empty(orphaned);
    }

    [Theory]
    [MemberData(nameof(AllSubclasses))]
    public void EverySubclassPairsItsCodeWithTheDocumentedKind(Type exceptionType)
    {
        var expected = Expected[exceptionType];
        var exception = Construct(exceptionType);

        Assert.Equal(expected.ReasonCode, exception.ReasonCode);
        Assert.Equal(expected.Kind, exception.Kind);
    }

    // Guards against the other half of the old hole: a subclass passing a string
    // literal instead of a catalogue constant. Checked against both catalogue
    // files, since the codes are split (see ReasonCodes' header).
    [Theory]
    [MemberData(nameof(AllSubclasses))]
    public void EverySubclassCodeExistsInTheCatalogue(Type exceptionType)
    {
        var known = CatalogueCodes();
        var exception = Construct(exceptionType);

        Assert.Contains(exception.ReasonCode, known);
    }

    // The message is log-only, so nothing asserts its content — but it must
    // exist, or a log line about a rejected request says nothing at all.
    [Theory]
    [MemberData(nameof(AllSubclasses))]
    public void EverySubclassCarriesAMessageForTheLog(Type exceptionType)
    {
        var exception = Construct(exceptionType);

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    // Built through the single public constructor with default arguments: what is
    // under test is the kind/code pair the constructor hard-codes, not the values
    // interpolated into the message.
    private static AppException Construct(Type exceptionType)
    {
        var constructor = exceptionType.GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();

        return (AppException)constructor.Invoke(arguments);
    }

    private static IReadOnlyCollection<string> CatalogueCodes() =>
        new[] { typeof(ReasonCodes), typeof(AuthenticationFailureReason) }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
}
