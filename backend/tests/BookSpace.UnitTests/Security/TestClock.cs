using BookSpace.Application.Abstractions;

namespace BookSpace.UnitTests.Security;

// Fixed clock so token expiry is assertable without waiting. Kept in one place
// because both the token-service and handler tests need it.
internal sealed class TestClock : IClock
{
    public TestClock(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; set; }
}
