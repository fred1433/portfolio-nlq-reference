using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PortfolioNlq.Fixture;

public sealed record Tenant(int TenantId, string Name, string ReportingCurrency);
public sealed record Custodian(int CustodianId, string Name);
public sealed record Security(int SecurityId, string Symbol, string Name, string AssetClass, string Currency);
public sealed record SecurityTag(int SecurityId, string Tag);
public sealed record Price(int SecurityId, DateOnly PriceDate, decimal ClosePrice);
public sealed record FxRate(string Currency, DateOnly RateDate, decimal UsdPerUnit);
public sealed record Household(int HouseholdId, int TenantId, string Name);
public sealed record Model(int ModelId, int TenantId, string Name, string Kind);
public sealed record ModelComponent(int ModelId, int? ChildModelId, int? SecurityId, decimal Weight);
public sealed record Account(int AccountId, int TenantId, string AccountNumber, string Name, string AccountType,
    int? HouseholdId, int CustodianId, int ModelId, string BaseCurrency);
public sealed record OpeningPosition(int AccountId, int SecurityId, decimal Quantity, DateOnly AsOf);
public sealed record BlockOrder(int BlockId, int TenantId, int SecurityId, string Side, decimal OrderQuantity,
    DateOnly TradeDate, DateTime CreatedAt, string Status, string TimeInForce);
public sealed record Allocation(int AllocationId, int BlockId, int AccountId, decimal AllocatedQuantity,
    decimal CancelledQuantity, DateTime? CancelledAt);
public sealed record Execution(int ExecutionId, int AllocationId, decimal Quantity, decimal Price, DateTime ExecutedAt);
public sealed record Transaction(int TransactionId, int AccountId, int SecurityId, decimal Quantity,
    DateOnly TradeDate, DateOnly SettleDate, string Type, int? ExecutionId);

/// <summary>
/// The frozen, synthetic fixture. Every input the answers depend on lives in fixture/*.csv;
/// <see cref="Sha256"/> identifies the exact bytes and is written into every trace.
/// </summary>
public sealed record FixtureSet
{
    public required IReadOnlyList<Tenant> Tenants { get; init; }
    public required IReadOnlyList<Custodian> Custodians { get; init; }
    public required IReadOnlyList<Security> Securities { get; init; }
    public required IReadOnlyList<SecurityTag> SecurityTags { get; init; }
    public required IReadOnlyList<Price> Prices { get; init; }
    public required IReadOnlyList<FxRate> FxRates { get; init; }
    public required IReadOnlyList<Household> Households { get; init; }
    public required IReadOnlyList<Model> Models { get; init; }
    public required IReadOnlyList<ModelComponent> ModelComponents { get; init; }
    public required IReadOnlyList<Account> Accounts { get; init; }
    public required IReadOnlyList<OpeningPosition> OpeningPositions { get; init; }
    public required IReadOnlyList<BlockOrder> BlockOrders { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public required IReadOnlyList<Execution> Executions { get; init; }
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required string Sha256 { get; init; }

    public string FixtureId => "synthetic-2026-10-06-" + Sha256[..12];

    public static readonly string[] Files =
    [
        "tenants.csv", "custodians.csv", "securities.csv", "security_tags.csv", "prices.csv", "fx_rates.csv",
        "households.csv", "models.csv", "model_components.csv", "accounts.csv", "opening_positions.csv",
        "block_orders.csv", "allocations.csv", "executions.csv", "transactions.csv",
    ];

    public static FixtureSet Load(string directory)
    {
        var tables = Files.ToDictionary(f => f, f => Csv.Read(Path.Combine(directory, f)));
        return new FixtureSet
        {
            Tenants = tables["tenants.csv"].Select(r => new Tenant(I(r, "tenant_id"), r["name"], r["reporting_currency"])).ToList(),
            Custodians = tables["custodians.csv"].Select(r => new Custodian(I(r, "custodian_id"), r["name"])).ToList(),
            Securities = tables["securities.csv"].Select(r => new Security(I(r, "security_id"), r["symbol"], r["name"], r["asset_class"], r["currency"])).ToList(),
            SecurityTags = tables["security_tags.csv"].Select(r => new SecurityTag(I(r, "security_id"), r["tag"])).ToList(),
            Prices = tables["prices.csv"].Select(r => new Price(I(r, "security_id"), Dt(r, "price_date"), M(r, "close_price"))).ToList(),
            FxRates = tables["fx_rates.csv"].Select(r => new FxRate(r["currency"], Dt(r, "rate_date"), M(r, "usd_per_unit"))).ToList(),
            Households = tables["households.csv"].Select(r => new Household(I(r, "household_id"), I(r, "tenant_id"), r["name"])).ToList(),
            Models = tables["models.csv"].Select(r => new Model(I(r, "model_id"), I(r, "tenant_id"), r["name"], r["kind"])).ToList(),
            ModelComponents = tables["model_components.csv"].Select(r => new ModelComponent(I(r, "model_id"), NI(r, "child_model_id"), NI(r, "security_id"), M(r, "weight"))).ToList(),
            Accounts = tables["accounts.csv"].Select(r => new Account(I(r, "account_id"), I(r, "tenant_id"), r["account_number"], r["name"], r["account_type"],
                NI(r, "household_id"), I(r, "custodian_id"), I(r, "model_id"), r["base_currency"])).ToList(),
            OpeningPositions = tables["opening_positions.csv"].Select(r => new OpeningPosition(I(r, "account_id"), I(r, "security_id"), M(r, "quantity"), Dt(r, "as_of"))).ToList(),
            BlockOrders = tables["block_orders.csv"].Select(r => new BlockOrder(I(r, "block_id"), I(r, "tenant_id"), I(r, "security_id"), r["side"], M(r, "order_quantity"),
                Dt(r, "trade_date"), Ts(r["created_at"])!.Value, r["status"], r["time_in_force"])).ToList(),
            Allocations = tables["allocations.csv"].Select(r => new Allocation(I(r, "allocation_id"), I(r, "block_id"), I(r, "account_id"), M(r, "allocated_quantity"),
                M(r, "cancelled_quantity"), Ts(r["cancelled_at"]))).ToList(),
            Executions = tables["executions.csv"].Select(r => new Execution(I(r, "execution_id"), I(r, "allocation_id"), M(r, "quantity"), M(r, "price"), Ts(r["executed_at"])!.Value)).ToList(),
            Transactions = tables["transactions.csv"].Select(r => new Transaction(I(r, "transaction_id"), I(r, "account_id"), I(r, "security_id"), M(r, "quantity"),
                Dt(r, "trade_date"), Dt(r, "settle_date"), r["type"], NI(r, "execution_id"))).ToList(),
            Sha256 = ComputeSha256(directory),
        }.CheckConventions();
    }

    /// <summary>Whole units for every security quantity; only cash balances carry decimals.</summary>
    public FixtureSet CheckConventions()
    {
        static void Whole(decimal q, string what) { if (q != decimal.Truncate(q)) throw new InvalidDataException($"{what}: {q} is not a whole number of units"); }
        bool IsCash(int securityId) => Securities.Single(x => x.SecurityId == securityId).AssetClass == "Cash";
        foreach (var b in BlockOrders) Whole(b.OrderQuantity, $"block {b.BlockId}");
        foreach (var a in Allocations) { Whole(a.AllocatedQuantity, $"allocation {a.AllocationId}"); Whole(a.CancelledQuantity, $"allocation {a.AllocationId} cancelled"); }
        foreach (var e in Executions) Whole(e.Quantity, $"execution {e.ExecutionId}");
        foreach (var o in OpeningPositions.Where(o => !IsCash(o.SecurityId))) Whole(o.Quantity, $"position {o.AccountId}/{o.SecurityId}");
        foreach (var t in Transactions.Where(t => !IsCash(t.SecurityId))) Whole(t.Quantity, $"transaction {t.TransactionId}");
        return this;
    }

    /// <summary>SHA-256 over every fixture file, in a fixed order, line endings normalised to LF.</summary>
    public static string ComputeSha256(string directory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Files)
        {
            var text = File.ReadAllText(Path.Combine(directory, f)).Replace("\r\n", "\n");
            sha.AppendData(Encoding.UTF8.GetBytes(f + "\n"));
            sha.AppendData(Encoding.UTF8.GetBytes(text));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public int TenantOfBlock(int blockId) => BlockOrders.Single(b => b.BlockId == blockId).TenantId;
    public int TenantOfAccount(int accountId) => Accounts.Single(a => a.AccountId == accountId).TenantId;

    static int I(IReadOnlyDictionary<string, string> r, string k) => int.Parse(r[k], CultureInfo.InvariantCulture);
    static int? NI(IReadOnlyDictionary<string, string> r, string k) => string.IsNullOrEmpty(r[k]) ? null : I(r, k);
    static decimal M(IReadOnlyDictionary<string, string> r, string k) => decimal.Parse(r[k], NumberStyles.Number, CultureInfo.InvariantCulture);
    static DateOnly Dt(IReadOnlyDictionary<string, string> r, string k) => DateOnly.ParseExact(r[k], "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static DateTime? Ts(string s) => string.IsNullOrEmpty(s) ? null : DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>Minimal RFC 4180 reader (quoted fields, doubled quotes). The fixture is small and hand-checked.</summary>
public static class Csv
{
    public static List<IReadOnlyDictionary<string, string>> Read(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        var header = Split(lines[0]);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var cells = Split(line);
            if (cells.Count != header.Count) throw new InvalidDataException($"{Path.GetFileName(path)}: expected {header.Count} cells, got {cells.Count}: {line}");
            rows.Add(header.Zip(cells).ToDictionary(p => p.First, p => p.Second));
        }
        return rows;
    }

    static List<string> Split(string line)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
