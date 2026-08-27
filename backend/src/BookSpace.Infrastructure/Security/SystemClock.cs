using BookSpace.Application.Abstractions;

namespace BookSpace.Infrastructure.Security;

internal sealed class SystemClock : IClock
{
    // CLAUDE.md §4.3: every instant in this system is UTC.
    public DateTime UtcNow => DateTime.UtcNow;
}
