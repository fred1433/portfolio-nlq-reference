using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioNlq.Fixture;

namespace PortfolioNlq.Reference;

/// <summary>
/// What a correct answer is, written by a person for each evaluation case, independently of the model output.
/// Names are resolved here against the asking tenant only, by exact match.
/// </summary>
public sealed class CaseSpec
{
    public string Measure { get; set; } = "";                 // drift | open_allocations
    public string? DimensionType { get; set; }               // asset_class | tag
    public string? DimensionValue { get; set; }
    public string? AsOf { get; set; }                        // yyyy-MM-dd
    public string Basis { get; set; } = "trade";             // trade | settlement
    public string? ThresholdDirection { get; set; }          // above | below | either
    public decimal? ThresholdPoints { get; set; }
    public List<string> GroupBy { get; set; } = [];
    public string? Side { get; set; }                         // Buy | Sell
    public string? AssetClass { get; set; }
    public string Status { get; set; } = "open";              // open | all
    public List<string> AccountNumbers { get; set; } = [];
    public List<string> AccountNames { get; set; } = [];
    public List<string> HouseholdNames { get; set; } = [];
    public List<string> CustodianNames { get; set; } = [];
    public List<string> ModelNames { get; set; } = [];
    public List<string> Symbols { get; set; } = [];
}

public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";               // answer | clarification | refusal | authorization
    public string Family { get; set; } = "";
    public string Set { get; set; } = "tuning";              // tuning | holdout | regression
    public int TenantId { get; set; }
    public string Question { get; set; } = "";
    public string? Why { get; set; }
    public CaseSpec? Spec { get; set; }
}

public sealed class EvalFile
{
    public string ReferenceClock { get; set; } = "";
    public string Timezone { get; set; } = "";
    public List<EvalCase> Cases { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static EvalFile Load(string path) => JsonSerializer.Deserialize<EvalFile>(File.ReadAllText(path), Json)!;
}

public sealed record ExpectedRow(string Key, string Status, IReadOnlyDictionary<string, string?> Values, string? Reason);

public sealed record ExpectedCase(string CaseId, int TenantId, string Category, string Outcome,
    IReadOnlyList<string> KeyColumns, IReadOnlyList<ExpectedRow> Rows);

/// <summary>
/// Computes expected results straight from the fixture rows with LINQ and System.Decimal.
/// It does not call the reporting views, the SQL compiler or the catalog: a bug there cannot hide here.
/// </summary>
public sealed class ReferenceCalculator(FixtureSet f)
{
    public ExpectedCase Compute(EvalCase c)
    {
        if (c.Category is "clarification") return new(c.Id, c.TenantId, c.Category, "clarify", [], []);
        if (c.Category is "refusal") return new(c.Id, c.TenantId, c.Category, "refuse", [], []);
        if (c.Category is "authorization" && c.Spec is null) return new(c.Id, c.TenantId, c.Category, "nothing", [], []);
        var spec = c.Spec ?? throw new InvalidDataException($"{c.Id}: answer and authorization cases need a spec");
        return spec.Measure switch
        {
            "drift" => Drift(c, spec),
            "open_allocations" => Allocations(c, spec),
            _ => throw new InvalidDataException($"{c.Id}: unknown measure {spec.Measure}"),
        };
    }

    // ---------- drift against model target ----------

    ExpectedCase Drift(EvalCase c, CaseSpec s)
    {
        var asOf = DateOnly.ParseExact(s.AsOf!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var accounts = FilterAccounts(c.TenantId, s).ToList();
        var inDim = SecuritiesInDimension(s.DimensionType!, s.DimensionValue!);

        var perAccount = accounts.Select(a =>
        {
            var holdings = Holdings(a.AccountId, asOf, s.Basis);
            var missing = new List<string>();
            decimal total = 0, dim = 0;
            foreach (var (secId, qty) in holdings.OrderBy(h => h.Key))
            {
                var sec = f.Securities.Single(x => x.SecurityId == secId);
                var px = f.Prices.SingleOrDefault(p => p.SecurityId == secId && p.PriceDate == asOf);
                var fx = f.FxRates.SingleOrDefault(r => r.Currency == sec.Currency && r.RateDate == asOf);
                if (px is null) { missing.Add($"no close for {sec.Symbol} on {asOf:yyyy-MM-dd}"); continue; }
                if (fx is null) { missing.Add($"no {sec.Currency}/USD rate on {asOf:yyyy-MM-dd}"); continue; }
                var mv = qty * px.ClosePrice * fx.UsdPerUnit;
                total += mv;
                if (inDim.Contains(secId)) dim += mv;
            }
            var target = FlattenTargets(a.ModelId, 1m).Where(t => inDim.Contains(t.Key)).Sum(t => t.Value);
            return (Account: a, Total: total, Dim: dim, Target: target, Missing: missing);
        }).ToList();

        var rows = new List<ExpectedRow>();
        if (s.GroupBy.SequenceEqual(["household"]))
        {
            foreach (var g in perAccount.Where(p => p.Account.HouseholdId is not null).GroupBy(p => p.Account.HouseholdId!.Value))
            {
                var name = f.Households.Single(h => h.HouseholdId == g.Key).Name;
                var missing = g.SelectMany(p => p.Missing).ToList();
                if (missing.Count > 0) { rows.Add(Incomplete(name, missing)); continue; }
                var total = g.Sum(p => p.Total);
                var weight = g.Sum(p => p.Dim) * 100m / total;
                var target = g.Sum(p => p.Target * p.Total) * 100m / total;
                AddIfBreach(rows, name, total, weight, target, s);
            }
        }
        else
        {
            foreach (var p in perAccount)
            {
                if (p.Missing.Count > 0) { rows.Add(Incomplete(p.Account.AccountNumber, p.Missing)); continue; }
                AddIfBreach(rows, p.Account.AccountNumber, p.Total, p.Dim * 100m / p.Total, p.Target * 100m, s);
            }
        }
        var key = s.GroupBy.SequenceEqual(["household"]) ? "household" : "account_number";
        return new(c.Id, c.TenantId, c.Category, "rows", [key], rows.OrderBy(r => r.Key, StringComparer.Ordinal).ToList());
    }

    static ExpectedRow Incomplete(string key, List<string> missing) =>
        new(key, "Incomplete", new Dictionary<string, string?>(), string.Join("; ", missing.Distinct()));

    static void AddIfBreach(List<ExpectedRow> rows, string key, decimal total, decimal weight, decimal target, CaseSpec s)
    {
        var drift = weight - target;
        var breach = s.ThresholdDirection switch
        {
            null => true,
            "above" => drift > s.ThresholdPoints!.Value,
            "below" => drift < -s.ThresholdPoints!.Value,
            "either" => Math.Abs(drift) > s.ThresholdPoints!.Value,
            _ => throw new InvalidDataException("threshold direction " + s.ThresholdDirection),
        };
        if (!breach) return;
        rows.Add(new(key, "Complete", new Dictionary<string, string?>
        {
            ["total_mv_usd"] = D(total), ["weight_pct"] = D(weight), ["target_pct"] = D(target), ["drift_pts"] = D(drift),
        }, null));
    }

    /// <summary>Quantity per security: opening settled positions plus every transaction dated on or before the as-of date on the chosen basis.</summary>
    Dictionary<int, decimal> Holdings(int accountId, DateOnly asOf, string basis)
    {
        var h = new Dictionary<int, decimal>();
        foreach (var p in f.OpeningPositions.Where(p => p.AccountId == accountId && p.AsOf <= asOf))
            h[p.SecurityId] = h.GetValueOrDefault(p.SecurityId) + p.Quantity;
        foreach (var t in f.Transactions.Where(t => t.AccountId == accountId))
        {
            var d = basis == "settlement" ? t.SettleDate : t.TradeDate;
            if (d <= asOf) h[t.SecurityId] = h.GetValueOrDefault(t.SecurityId) + t.Quantity;
        }
        return h.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Security weights of a model, multiplying weights down the hierarchy (any depth).</summary>
    Dictionary<int, decimal> FlattenTargets(int modelId, decimal scale)
    {
        var result = new Dictionary<int, decimal>();
        foreach (var c in f.ModelComponents.Where(c => c.ModelId == modelId))
        {
            if (c.ChildModelId is int child)
                foreach (var (sec, w) in FlattenTargets(child, scale * c.Weight)) result[sec] = result.GetValueOrDefault(sec) + w;
            else result[c.SecurityId!.Value] = result.GetValueOrDefault(c.SecurityId!.Value) + scale * c.Weight;
        }
        return result;
    }

    HashSet<int> SecuritiesInDimension(string type, string value) => type switch
    {
        "asset_class" => f.Securities.Where(s => s.AssetClass == value).Select(s => s.SecurityId).ToHashSet(),
        "tag" => f.SecurityTags.Where(t => t.Tag == value).Select(t => t.SecurityId).ToHashSet(),
        _ => throw new InvalidDataException("dimension " + type),
    };

    IEnumerable<Account> FilterAccounts(int tenantId, CaseSpec s) => f.Accounts.Where(a => a.TenantId == tenantId)
        .Where(a => s.AccountNumbers.Count == 0 || s.AccountNumbers.Contains(a.AccountNumber))
        .Where(a => s.AccountNames.Count == 0 || s.AccountNames.Contains(a.Name))
        .Where(a => s.HouseholdNames.Count == 0 || (a.HouseholdId is int h && s.HouseholdNames.Contains(f.Households.Single(x => x.HouseholdId == h && x.TenantId == tenantId).Name)))
        .Where(a => s.CustodianNames.Count == 0 || s.CustodianNames.Contains(f.Custodians.Single(x => x.CustodianId == a.CustodianId).Name))
        .Where(a => s.ModelNames.Count == 0 || s.ModelNames.Contains(f.Models.Single(x => x.ModelId == a.ModelId).Name));

    // ---------- open allocations ----------

    ExpectedCase Allocations(EvalCase c, CaseSpec s)
    {
        var accounts = FilterAccounts(c.TenantId, s).ToDictionary(a => a.AccountId);
        var lines = (from al in f.Allocations
                     join b in f.BlockOrders on al.BlockId equals b.BlockId
                     join sec in f.Securities on b.SecurityId equals sec.SecurityId
                     where b.TenantId == c.TenantId && accounts.ContainsKey(al.AccountId)
                     where s.Side is null || b.Side == s.Side
                     where s.AssetClass is null || sec.AssetClass == s.AssetClass
                     where s.Symbols.Count == 0 || s.Symbols.Contains(sec.Symbol)
                     let executed = f.Executions.Where(e => e.AllocationId == al.AllocationId).Sum(e => e.Quantity)
                     let remaining = al.AllocatedQuantity - executed - al.CancelledQuantity
                     where s.Status == "all" || remaining > 0
                     let acct = accounts[al.AccountId]
                     select new AllocLine(al, b, sec, acct, executed, remaining,
                         f.Custodians.Single(x => x.CustodianId == acct.CustodianId).Name,
                         remaining > 0 ? (executed > 0 ? "Partially filled" : "Working") : (al.CancelledQuantity > 0 ? "Remainder cancelled" : "Filled"))).ToList();

        string[] order = ["account_number", "symbol", "custodian", "block_id"];
        var groupCols = order.Where(o => s.GroupBy.Contains(o)).ToList();
        string KeyOf(AllocLine l) => string.Join(" | ", groupCols.Select(g => g switch
        {
            "account_number" => l.Acct.AccountNumber,
            "symbol" => l.Sec.Symbol,
            "custodian" => l.Custodian,
            "block_id" => l.B.BlockId.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException(g),
        }));

        var rows = lines.GroupBy(l => KeyOf(l)).Select(g =>
        {
            var statuses = g.Select(x => x.Status).Distinct().ToList();
            return new ExpectedRow(g.Key, "Complete", new Dictionary<string, string?>
            {
                ["allocated_qty"] = D(g.Sum(x => x.Al.AllocatedQuantity)),
                ["executed_qty"] = D(g.Sum(x => x.Executed)),
                ["cancelled_qty"] = D(g.Sum(x => x.Al.CancelledQuantity)),
                ["remaining_qty"] = D(g.Sum(x => x.Remaining)),
                ["order_status"] = statuses.Count == 1 ? statuses[0] : "Mixed",
                ["allocation_count"] = g.Count().ToString(CultureInfo.InvariantCulture),
            }, null);
        }).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        return new(c.Id, c.TenantId, c.Category, "rows", groupCols, rows);
    }

    sealed record AllocLine(Allocation Al, BlockOrder B, Security Sec, Account Acct, decimal Executed, decimal Remaining, string Custodian, string Status);

    static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
