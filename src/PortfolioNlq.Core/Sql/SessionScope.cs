using Microsoft.Data.SqlClient;

namespace PortfolioNlq.Sql;

/// <summary>The authenticated identity the host application hands over. The model never sees or sets it.</summary>
public sealed record ScopeIdentity(int TenantId, string UserId);

public sealed class ScopeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Opens a connection as the restricted reporting login and pins the tenant in SESSION_CONTEXT with @read_only = 1.
/// If the context cannot be set and read back, the connection is closed and nothing runs.
/// </summary>
public static class SessionScope
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, ScopeIdentity scope, CancellationToken ct,
        Func<SqlConnection, CancellationToken, Task>? afterOpen = null)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            if (afterOpen is not null) await afterOpen(conn, ct); // test seam: simulates a cancellation between open and scope setup
            await using (var set = new SqlCommand(
                "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 1; " +
                "EXEC sys.sp_set_session_context @key = N'user_id', @value = @user, @read_only = 1;", conn))
            {
                set.Parameters.AddWithValue("@tenant", scope.TenantId);
                set.Parameters.AddWithValue("@user", scope.UserId);
                await set.ExecuteNonQueryAsync(ct);
            }
            await using var check = new SqlCommand("SELECT CAST(SESSION_CONTEXT(N'tenant_id') AS int)", conn);
            var pinned = await check.ExecuteScalarAsync(ct);
            if (pinned is not int t || t != scope.TenantId)
                throw new ScopeException($"tenant scope could not be verified (read back {pinned ?? "null"})");
            return conn;
        }
        catch (Exception e)
        {
            await conn.DisposeAsync(); // released on every unsuccessful exit, cancellation included
            if (e is OperationCanceledException) throw;
            throw e as ScopeException ?? new ScopeException("tenant scope could not be set: " + e.Message, e);
        }
    }
}
