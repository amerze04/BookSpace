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
//
// Two named exceptions may call Enter(), and CLAUDE.md §4.2's own restriction
// ("IgnoreQueryFilters() is allowed only in explicitly named ... methods")
// covers both for the same reason: AuthenticationUserRepository runs before a
// tenant is known (login, refresh, activation), and — since WP-8 Phase 2,
// docs/wp8-plan.md decision D9 — NotificationRepository runs with no tenant
// at all, because a background job's one dispatch run claims due
// notifications across every organisation in a single tick and there is no
// ICurrentTenant to read one from. No third caller is sanctioned without the
// same review.
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
