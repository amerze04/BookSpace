using System.Data;
using System.Data.Common;
using BookSpace.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BookSpace.Infrastructure.Persistence;

// CLAUDE.md §4.2, mechanism 3: SQL Server row-level security via
// sp_set_session_context, applied on every connection open. Fires once per
// logical DbConnection.Open() call, not once per physical socket — SQL
// Server's sp_reset_connection runs automatically when a pooled connection is
// handed back out, clearing any prior SESSION_CONTEXT, so re-setting it here
// on every open is the correct, standard pattern for EF Core + SQL Server RLS
// and never leaks one request's tenant into another's reused connection.
//
// Three keys, read by Security.fn_TenantAccessPredicate
// (see the AddTenantIsolationRls migration):
//   TenantInit   — always 1. Without this, a null-OrgId SysAdmin row and a
//                  connection nobody ever called sp_set_session_context on
//                  are indistinguishable to a null-safe predicate, which
//                  would leak SysAdmin Users rows to any uninitialized
//                  connection. Requiring TenantInit = 1 closes that gap.
//   OrgId        — the current tenant, or NULL (no tenant context).
//   TenantBypass — 1 only inside a TenantBypassScope.Enter() block (today:
//                  only AuthenticationUserRepository). Deliberately not
//                  "OrgId IS NULL => allow all" — that would invert the
//                  fail-closed default for every forgotten-context
//                  connection, not just the sanctioned bypass paths.
internal sealed class TenantSessionContextInterceptor : DbConnectionInterceptor
{
    private readonly ICurrentTenant _currentTenant;

    public TenantSessionContextInterceptor(ICurrentTenant currentTenant)
    {
        _currentTenant = currentTenant;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetSessionContextAsync((SqlConnection)connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetSessionContextAsync((SqlConnection)connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private async Task SetSessionContextAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "EXEC sys.sp_set_session_context @key = N'TenantInit',   @value = 1,       @read_only = 1;" +
            "EXEC sys.sp_set_session_context @key = N'OrgId',        @value = @orgId,  @read_only = 1;" +
            "EXEC sys.sp_set_session_context @key = N'TenantBypass', @value = @bypass, @read_only = 1;";

        command.Parameters.Add(new SqlParameter("@orgId", SqlDbType.UniqueIdentifier)
        {
            Value = (object?)_currentTenant.OrgId ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@bypass", SqlDbType.Bit)
        {
            Value = TenantBypassScope.IsActive ? 1 : 0,
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
