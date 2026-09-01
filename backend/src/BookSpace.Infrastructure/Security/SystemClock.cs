using BookSpace.Application.Abstractions;

namespace BookSpace.Infrastructure.Security;

internal sealed class SystemClock : IClock
{
    // CLAUDE.md §4.3: every instant in this system is UTC, and every instant
    // column is datetime2(0) — whole seconds.
    //
    // Truncated to the second for that second reason. datetime2(0) *rounds* on
    // the way in, so a value stamped at 12:00:32.9 is stored as 12:00:33: the
    // entity in memory and the row on disk then disagree, and a create response
    // built from the entity does not match the resource a client immediately
    // reads back. Found exactly that way, by a WP-3 Phase 2 test comparing the
    // 201 body with the following GET.
    //
    // Truncating rather than rounding, so the application never reports an
    // instant that has not happened yet. Nothing here needs sub-second
    // precision: token lifetimes are minutes and days, `exp` is Unix seconds,
    // and every audited column is whole seconds.
    public DateTime UtcNow => new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
        DateTimeKind.Utc);
}
