using Microsoft.Data.SqlClient;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.SqlTests;

/// <summary>
/// Direct tests of the database boundary under the restricted reporting login, with no model and no prompt involved.
/// </summary>
public class SecurityTests
{
    static readonly ScopeIdentity Larkspur = new(1, "test-user-1");
    static readonly ScopeIdentity Halyard = new(2, "test-user-2");

    static async Task<List<string>> Strings(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync()) list.Add(Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return list;
    }

    [SqlFact]
    public async Task Authorized_rows_are_returned()
    {
        await using var c = await SessionScope.OpenAsync(Db.Reader!, Larkspur, default);
        var accounts = await Strings(c, "SELECT account_number FROM rpt.v_accounts ORDER BY account_number");
        Assert.Equal(["LWP-1001", "LWP-1002", "LWP-1003", "LWP-1004", "LWP-1005", "LWP-1006"], accounts);
    }

    [SqlFact]
    public async Task Other_tenant_rows_are_excluded_even_when_named_directly()
    {
        await using var c = await SessionScope.OpenAsync(Db.Reader!, Larkspur, default);
        Assert.Empty(await Strings(c, "SELECT account_number FROM rpt.v_accounts WHERE account_number = 'HCC-2002'"));
        Assert.Empty(await Strings(c, "SELECT allocation_id FROM rpt.v_allocation_lines WHERE account_number LIKE 'HCC-%'"));
        Assert.Equal(["LWP-1001"], await Strings(c, "SELECT account_number FROM rpt.v_accounts WHERE account_name = N'Whitfield Family Trust'"));
    }

    [SqlFact]
    public async Task Missing_identity_fails_the_query_instead_of_returning_nothing()
    {
        await using var c = new SqlConnection(Db.Reader);
        await c.OpenAsync();
        var e = await Assert.ThrowsAsync<SqlException>(() => Strings(c, "SELECT account_number FROM rpt.v_accounts"));
        Assert.Contains("tenant scope missing", e.Message);
    }

    [SqlFact]
    public async Task Session_context_cannot_be_changed_once_pinned()
    {
        await using var c = await SessionScope.OpenAsync(Db.Reader!, Larkspur, default);
        await using var cmd = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = 2;", c);
        await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(["1"], await Strings(c, "SELECT CAST(SESSION_CONTEXT(N'tenant_id') AS int)"));
        Assert.DoesNotContain("HCC-2002", await Strings(c, "SELECT account_number FROM rpt.v_accounts"));
    }

    [SqlFact]
    public async Task A_reused_pooled_connection_keeps_the_right_scope()
    {
        var pooled = new SqlConnectionStringBuilder(Db.Reader) { MaxPoolSize = 1, ApplicationName = "nlq-pool-test" }.ConnectionString;
        string spid1, spid2, spid3;
        await using (var c = await SessionScope.OpenAsync(pooled, Larkspur, default))
            spid1 = (await Strings(c, "SELECT @@SPID"))[0];
        await using (var c = await SessionScope.OpenAsync(pooled, Halyard, default))
        {
            spid2 = (await Strings(c, "SELECT @@SPID"))[0];
            var accounts = await Strings(c, "SELECT account_number FROM rpt.v_accounts");
            Assert.All(accounts, a => Assert.StartsWith("HCC-", a));
            Assert.Equal(3, accounts.Count);
        }
        await using (var c = new SqlConnection(pooled))
        {
            await c.OpenAsync();
            spid3 = (await Strings(c, "SELECT @@SPID"))[0];
            Assert.Equal([""], await Strings(c, "SELECT COALESCE(CAST(SESSION_CONTEXT(N'tenant_id') AS varchar(10)), '')"));
            await Assert.ThrowsAsync<SqlException>(() => Strings(c, "SELECT account_number FROM rpt.v_accounts"));
        }
        Assert.Equal(spid1, spid2); // same physical connection, reset by the pool between uses
        Assert.Equal(spid2, spid3);
    }

    [SqlFact]
    public async Task Aggregates_and_joins_stay_filtered()
    {
        await using var c = await SessionScope.OpenAsync(Db.Reader!, Larkspur, default);
        Assert.Equal(["9", "2720"], await Strings(c, "SELECT CAST(COUNT(*) AS varchar(10)) FROM rpt.v_allocation_lines UNION ALL SELECT CAST(CAST(SUM(allocated_quantity) AS int) AS varchar(10)) FROM rpt.v_allocation_lines"));
        Assert.Equal(["10"], await Strings(c, "SELECT COUNT(*) FROM rpt.v_executions"));
        var positions = await Strings(c, "SELECT DISTINCT a.account_number FROM rpt.fn_positions('2026-10-06', 'trade') AS p JOIN rpt.v_accounts AS a ON a.account_id = p.account_id");
        Assert.All(positions, a => Assert.StartsWith("LWP-", a));
        Assert.Empty(await Strings(c, "SELECT account_id FROM rpt.fn_positions('2026-10-06', 'trade') WHERE account_id > 200"));
        Assert.Empty(await Strings(c, "SELECT account_id FROM rpt.fn_account_targets() WHERE account_id > 200"));
    }

    [SqlFact]
    public async Task Writes_policy_changes_and_base_tables_are_denied()
    {
        await using var c = await SessionScope.OpenAsync(Db.Reader!, Larkspur, default);
        string[] attempts =
        [
            "INSERT INTO rpt.v_households (household_id, household) VALUES (99, N'x')",
            "UPDATE rpt.v_accounts SET account_name = N'x'",
            "DELETE FROM rpt.v_executions",
            "DELETE FROM dbo.allocation",
            "SELECT TOP 1 account_number FROM dbo.account",
            "ALTER SECURITY POLICY sec.tenant_isolation WITH (STATE = OFF)",
            "DROP SECURITY POLICY sec.tenant_isolation",
            "CREATE TABLE dbo.scratch (id int)",
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = NULL",
        ];
        foreach (var sql in attempts)
        {
            await using var cmd = new SqlCommand(sql, c);
            await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        }
        Assert.Equal(6, (await Strings(c, "SELECT account_number FROM rpt.v_accounts")).Count);
    }

    [SqlFact]
    public async Task Name_lookup_runs_under_the_same_scope()
    {
        var dir = await new SqlEntityDirectory(Db.Reader!).LoadAsync(Larkspur, default);
        Assert.DoesNotContain(dir.Accounts, a => a.Code!.StartsWith("HCC-", StringComparison.Ordinal));
        Assert.Single(dir.Accounts, a => a.Name == "Whitfield Family Trust");
        Assert.Empty(NameMatcher.Match("Halyard Creek Offshore Ltd", dir.Accounts));
    }

    [SqlFact]
    public async Task A_timeout_is_reported_as_a_timeout_never_as_an_empty_result()
    {
        var exec = new ScopedQueryExecutor(Db.Reader!, new ExecutorOptions(CommandTimeoutSeconds: 1));
        var r = await exec.ExecuteAsync(Larkspur, new CompiledQuery("WAITFOR DELAY '00:00:05'; SELECT 1 AS x;", [], null, []), default);
        Assert.Equal("timeout", r.Status);
        Assert.Empty(r.Rows);
        Assert.Contains("none should be inferred", r.Describe());
    }

    [SqlFact]
    public async Task Cancellation_is_reported_as_cancelled()
    {
        var exec = new ScopedQueryExecutor(Db.Reader!);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var r = await exec.ExecuteAsync(Larkspur, new CompiledQuery("WAITFOR DELAY '00:00:10'; SELECT 1 AS x;", [], null, []), cts.Token);
        Assert.Equal("cancelled", r.Status);
        Assert.Empty(r.Rows);
    }

    [SqlFact]
    public async Task Hitting_the_row_cap_is_reported_as_truncated()
    {
        var exec = new ScopedQueryExecutor(Db.Reader!, new ExecutorOptions(RowCap: 2));
        var r = await exec.ExecuteAsync(Larkspur, new CompiledQuery("SELECT account_number FROM rpt.v_accounts ORDER BY account_number", [], null, []), default);
        Assert.Equal("truncated", r.Status);
        Assert.Equal(2, r.Rows.Count);
        Assert.Contains("not a complete list", r.Describe());
    }
}
