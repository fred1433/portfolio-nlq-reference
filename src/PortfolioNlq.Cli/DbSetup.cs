using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using PortfolioNlq.Fixture;

namespace PortfolioNlq.Cli;

/// <summary>
/// Creates the database from db/*.sql, loads the CSV fixture, and creates the restricted reporting login.
/// Runs with an administrator connection; the answering path never uses that connection.
/// </summary>
public static partial class DbSetup
{
    public static async Task RunAsync(string adminConnection, string database, string readerLogin, string readerPassword, FixtureSet f, string? dbDir = null)
    {
        var master = new SqlConnectionStringBuilder(adminConnection) { InitialCatalog = "master" }.ConnectionString;
        await using (var conn = new SqlConnection(master))
        {
            await conn.OpenAsync();
            await Exec(conn, $"""
IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END;
CREATE DATABASE [{database}];
""");
            await using var cmd = new SqlCommand("""
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @login)
    EXEC (N'CREATE LOGIN ' + @quoted + N' WITH PASSWORD = ' + @pwd + N', CHECK_POLICY = ON');
ELSE
    EXEC (N'ALTER LOGIN ' + @quoted + N' WITH PASSWORD = ' + @pwd);
""", conn);
            cmd.Parameters.AddWithValue("@login", readerLogin);
            cmd.Parameters.AddWithValue("@quoted", "[" + readerLogin.Replace("]", "]]") + "]");
            cmd.Parameters.AddWithValue("@pwd", "N'" + readerPassword.Replace("'", "''") + "'");
            await cmd.ExecuteNonQueryAsync();
        }

        var dbConn = new SqlConnectionStringBuilder(adminConnection) { InitialCatalog = database }.ConnectionString;
        await using (var conn = new SqlConnection(dbConn))
        {
            await conn.OpenAsync();
            foreach (var file in Directory.GetFiles(dbDir ?? Repo.DbDir, "*.sql").Order(StringComparer.Ordinal))
                foreach (var batch in GoRegex().Split(await File.ReadAllTextAsync(file)).Where(b => b.Trim().Length > 0))
                    await Exec(conn, batch);
            await SeedAsync(conn, f);
            await Exec(conn, $"""
CREATE USER [{readerLogin}] FOR LOGIN [{readerLogin}];
ALTER ROLE nlq_reporting ADD MEMBER [{readerLogin}];
""");
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoRegex();

    static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SeedAsync(SqlConnection conn, FixtureSet f)
    {
        int TenantOfModel(int id) => f.Models.Single(m => m.ModelId == id).TenantId;
        int TenantOfAllocation(int id) => f.TenantOfBlock(f.Allocations.Single(a => a.AllocationId == id).BlockId);

        await Bulk(conn, "dbo.tenant", ["tenant_id", "name", "reporting_currency"], f.Tenants.Select(t => new object?[] { t.TenantId, t.Name, t.ReportingCurrency }));
        await Bulk(conn, "dbo.custodian", ["custodian_id", "name"], f.Custodians.Select(c => new object?[] { c.CustodianId, c.Name }));
        await Bulk(conn, "dbo.security_master", ["security_id", "symbol", "name", "asset_class", "currency"], f.Securities.Select(s => new object?[] { s.SecurityId, s.Symbol, s.Name, s.AssetClass, s.Currency }));
        await Bulk(conn, "dbo.security_tag", ["security_id", "tag"], f.SecurityTags.Select(t => new object?[] { t.SecurityId, t.Tag }));
        await Bulk(conn, "dbo.price", ["security_id", "price_date", "close_price"], f.Prices.Select(p => new object?[] { p.SecurityId, p.PriceDate.ToDateTime(TimeOnly.MinValue), p.ClosePrice }));
        await Bulk(conn, "dbo.fx_rate", ["currency", "rate_date", "usd_per_unit"], f.FxRates.Select(r => new object?[] { r.Currency, r.RateDate.ToDateTime(TimeOnly.MinValue), r.UsdPerUnit }));
        await Bulk(conn, "dbo.household", ["household_id", "tenant_id", "name"], f.Households.Select(h => new object?[] { h.HouseholdId, h.TenantId, h.Name }));
        await Bulk(conn, "dbo.model", ["model_id", "tenant_id", "name", "kind"], f.Models.Select(m => new object?[] { m.ModelId, m.TenantId, m.Name, m.Kind }));
        await Bulk(conn, "dbo.model_component", ["tenant_id", "model_id", "child_model_id", "security_id", "weight"],
            f.ModelComponents.Select(c => new object?[] { TenantOfModel(c.ModelId), c.ModelId, c.ChildModelId, c.SecurityId, c.Weight }));
        await Bulk(conn, "dbo.account", ["account_id", "tenant_id", "account_number", "name", "account_type", "household_id", "custodian_id", "model_id", "base_currency"],
            f.Accounts.Select(a => new object?[] { a.AccountId, a.TenantId, a.AccountNumber, a.Name, a.AccountType, a.HouseholdId, a.CustodianId, a.ModelId, a.BaseCurrency }));
        await Bulk(conn, "dbo.opening_position", ["tenant_id", "account_id", "security_id", "quantity", "as_of"],
            f.OpeningPositions.Select(p => new object?[] { f.TenantOfAccount(p.AccountId), p.AccountId, p.SecurityId, p.Quantity, p.AsOf.ToDateTime(TimeOnly.MinValue) }));
        await Bulk(conn, "dbo.block_order", ["block_id", "tenant_id", "security_id", "side", "order_quantity", "trade_date", "created_at", "status", "time_in_force"],
            f.BlockOrders.Select(b => new object?[] { b.BlockId, b.TenantId, b.SecurityId, b.Side, b.OrderQuantity, b.TradeDate.ToDateTime(TimeOnly.MinValue), b.CreatedAt, b.Status, b.TimeInForce }));
        await Bulk(conn, "dbo.allocation", ["allocation_id", "tenant_id", "block_id", "account_id", "allocated_quantity", "cancelled_quantity", "cancelled_at"],
            f.Allocations.Select(a => new object?[] { a.AllocationId, f.TenantOfBlock(a.BlockId), a.BlockId, a.AccountId, a.AllocatedQuantity, a.CancelledQuantity, a.CancelledAt }));
        await Bulk(conn, "dbo.execution", ["execution_id", "tenant_id", "allocation_id", "quantity", "price", "executed_at"],
            f.Executions.Select(e => new object?[] { e.ExecutionId, TenantOfAllocation(e.AllocationId), e.AllocationId, e.Quantity, e.Price, e.ExecutedAt }));
        await Bulk(conn, "dbo.txn", ["transaction_id", "tenant_id", "account_id", "security_id", "quantity", "trade_date", "settle_date", "type", "execution_id"],
            f.Transactions.Select(t => new object?[] { t.TransactionId, f.TenantOfAccount(t.AccountId), t.AccountId, t.SecurityId, t.Quantity,
                t.TradeDate.ToDateTime(TimeOnly.MinValue), t.SettleDate.ToDateTime(TimeOnly.MinValue), t.Type, t.ExecutionId }));
    }

    static async Task Bulk(SqlConnection conn, string table, string[] columns, IEnumerable<object?[]> rows)
    {
        // Parameterised inserts: small fixture, readable failures, foreign keys and checks enforced row by row.
        var sql = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))})";
        foreach (var row in rows)
        {
            await using var cmd = new SqlCommand(sql, conn);
            for (var i = 0; i < columns.Length; i++) cmd.Parameters.AddWithValue("@p" + i, row[i] ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
