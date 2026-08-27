namespace BookSpace.Infrastructure.Persistence;

// CLAUDE.md §4.2: EF's IgnoreQueryFilters() only skips the ORM-generated
// WHERE clause — it has no effect on SQL Server row-level security, which is
// enforced by the engine itself via SESSION_CONTEXT, independent of what LINQ
// produces. AuthenticationUserRepository (the one sanctioned
// IgnoreQueryFilters() caller, per its own header comment) needs to signal
// the RLS layer too, or its queries go from "unfiltered at the ORM layer" to
// "silently zero rows at the DB layer" instead. This is that signal.
//
// AsyncLocal flows through the awaited query it wraps, which is exactly how
// TenantSessionContextInterceptor observes it when the connection opens.
// Deliberately not DI-registered: every producer and the sole consumer live
// in this assembly, so a static keeps it out of service lifetimes entirely.
// Only AuthenticationUserRepository may call Enter() — see CLAUDE.md §4.2
// ("IgnoreQueryFilters() is allowed only in explicitly named ... methods");
// the same restriction applies here for the same reason.
internal static class TenantBypassScope
{
    private static readonly AsyncLocal<bool> IsActiveLocal = new();

    public static bool IsActive => IsActiveLocal.Value;

    public static IDisposable Enter()
    {
        IsActiveLocal.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => IsActiveLocal.Value = false;
    }
}
