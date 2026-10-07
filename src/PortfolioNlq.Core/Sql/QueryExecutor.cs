using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace PortfolioNlq.Sql;

public sealed record ExecutorOptions(int CommandTimeoutSeconds = 15, int RowCap = 500);

public sealed record ExecutionResult(
    string Status,                       // complete | truncated | timeout | cancelled | error
    IReadOnlyList<string> Columns,
    IReadOnlyList<Dictionary<string, string?>> Rows,
    IReadOnlyList<Dictionary<string, string?>> DetailRows,
    string? Error,
    long DurationMs)
{
    /// <summary>Text the answer shows next to the rows. A timeout is never presented as an empty result.</summary>
    public string Describe() => Status switch
    {
        "complete" => $"{Rows.Count} row(s), complete.",
        "truncated" => $"First {Rows.Count} rows only: the result was cut at the row cap, so this is not a complete list and no total is implied.",
        "timeout" => "The query did not finish within the time limit. No rows are shown and none should be inferred.",
        "cancelled" => "The query was cancelled. No rows are shown and none should be inferred.",
        _ => "The query failed: " + Error,
    };
}

public interface IQueryExecutor
{
    Task<ExecutionResult> ExecuteAsync(ScopeIdentity scope, CompiledQuery query, CancellationToken ct);
}

/// <summary>Runs compiled SQL under the pinned tenant scope with a timeout, a cancellation token and a row cap.</summary>
public sealed class ScopedQueryExecutor(string connectionString, ExecutorOptions? options = null) : IQueryExecutor
{
    readonly ExecutorOptions _o = options ?? new();

    public async Task<ExecutionResult> ExecuteAsync(ScopeIdentity scope, CompiledQuery query, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await SessionScope.OpenAsync(connectionString, scope, ct);
            var (cols, rows, truncated) = await RunAsync(conn, query.Sql, query.Parameters, ct);
            IReadOnlyList<Dictionary<string, string?>> detail = [];
            if (query.DetailSql is not null && !truncated)
            {
                (_, var lines, var linesTruncated) = await RunAsync(conn, query.DetailSql, query.Parameters, ct);
                detail = lines;
                truncated |= linesTruncated; // a cut anywhere means the answer is not complete
            }
            return new(truncated ? "truncated" : "complete", cols, rows, detail, null, sw.ElapsedMilliseconds);
        }
        catch (SqlException e) when (e.Number == -2)
        {
            return new("timeout", [], [], [], e.Message, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is OperationCanceledException || (e is SqlException se && ct.IsCancellationRequested))
        {
            return new("cancelled", [], [], [], e.Message, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is SqlException or ScopeException)
        {
            return new("error", [], [], [], e.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Runs one statement, reading at most RowCap + 1 rows to know whether the cap was hit.</summary>
    public async Task<(List<string> Columns, List<Dictionary<string, string?>> Rows, bool Truncated)> RunAsync(
        SqlConnection conn, string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _o.CommandTimeoutSeconds };
        foreach (var p in parameters) cmd.Parameters.Add(ToSqlParameter(p));
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var rows = new List<Dictionary<string, string?>>();
        var truncated = false;
        while (await r.ReadAsync(ct))
        {
            if (rows.Count == _o.RowCap) { truncated = true; cmd.Cancel(); break; }
            var row = new Dictionary<string, string?>();
            for (var i = 0; i < r.FieldCount; i++) row[cols[i]] = Text(r, i);
            rows.Add(row);
        }
        return (cols, rows, truncated);
    }

    /// <summary>Values are kept as exact invariant text: SqlDecimal keeps every digit SQL Server computed.</summary>
    static string? Text(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetProviderSpecificValue(i) switch
        {
            SqlDecimal d => TrimDecimal(d.ToString()),
            SqlDateTime dt => dt.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            var v => r.GetValue(i) switch
            {
                DateTime d2 => d2.TimeOfDay == TimeSpan.Zero ? d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d2.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var o => o.ToString(),
            },
        };
    }

    static string TrimDecimal(string s) => s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;

    public static SqlParameter ToSqlParameter(QueryParameter p)
    {
        var baseType = p.SqlType.Split('(')[0];
        return baseType switch
        {
            "int" => new SqlParameter(p.Name, SqlDbType.Int) { Value = int.Parse(p.Value, CultureInfo.InvariantCulture) },
            "date" => new SqlParameter(p.Name, SqlDbType.Date) { Value = DateTime.ParseExact(p.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture) },
            "datetime2" => new SqlParameter(p.Name, SqlDbType.DateTime2) { Scale = 0, Value = DateTime.ParseExact(p.Value, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
            "decimal" => new SqlParameter(p.Name, SqlDbType.Decimal) { Precision = 9, Scale = 4, Value = decimal.Parse(p.Value, CultureInfo.InvariantCulture) },
            "varchar" => new SqlParameter(p.Name, SqlDbType.VarChar, Size(p.SqlType)) { Value = p.Value },
            "nvarchar" => new SqlParameter(p.Name, SqlDbType.NVarChar, Size(p.SqlType)) { Value = p.Value },
            _ => throw new InvalidOperationException("parameter type " + p.SqlType),
        };
    }

    static int Size(string t) => int.Parse(t.Split('(', ')')[1], CultureInfo.InvariantCulture);
}
