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
    public string? AsOf { get; set; }                        // yyyy-MM-dd (drift close; for allocations, end of that day)
    public string? AsOfTime { get; set; }                    // yyyy-MM-ddTHH:mm:ss America/Chicago, allocations
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
    public List<string>? ExpectedOutcomes { get; set; }
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
    IReadOnlyList<string> KeyColumns, IReadOnlyList<ExpectedRow> Rows,
    IReadOnlyList<string> DetailKeyColumns, IReadOnlyList<ExpectedRow> DetailRows);

/// <summary>
/// Computes expected results straight from the fixture rows with LINQ and System.Decimal.
/// It does not call the reporting views, the SQL compiler or the catalog: a bug there cannot hide here.
/// Conventions it implements on its own: cash in the denominator, unpriced positions make an account incomplete,
/// an account with nothing to value is "No valuation", order events count only up to the cutoff,
/// a DAY order expires at its market's close (15:00 Chicago for USD, 10:30 Chicago for EUR).
/// </summary>
public sealed class ReferenceCalculator(FixtureSet f, DateTime? referenceLocalTime = null)
{
    readonly DateTime _now = referenceLocalTime ?? new DateTime(2026, 10, 7, 9, 0, 0);

    public ExpectedCase Compute(EvalCase c)
    {
        if (c.Category is "clarification") return Empty(c, "clarify");
        if (c.Category is "refusal") return Empty(c, "refuse");
        if (c.Category is "authorization" && c.Spec is null) return Empty(c, "nothing");
        var spec = c.Spec ?? throw new InvalidDataException($"{c.Id}: answer and authorization cases need a spec");
        return spec.Measure switch
        {
            "drift" => Drift(c, spec),
            "open_allocations" => Allocations(c, spec),
            _ => throw new InvalidDataException($"{c.Id}: unknown measure {spec.Measure}"),
        };
    }

    static ExpectedCase Empty(EvalCase c, string outcome) => new(c.Id, c.TenantId, c.Category, outcome, [], [], [], []);

    // ---------- drift against model target ----------

    sealed record AccountValue(Account Account, decimal Total, decimal Dim, decimal Target, List<string> Missing,
        Dictionary<int, decimal> Holdings, Dictionary<int, decimal?> Values, Dictionary<int, decimal> Targets)
    {
        public string Status => Missing.Count > 0 ? "Incomplete" : Total == 0 ? "No valuation" : "Complete";
    }

    AccountValue Value(Account a, DateOnly asOf, string basis, HashSet<int> inDim)
    {
        var holdings = Holdings(a.AccountId, asOf, basis);
        var missing = new List<string>();
        var values = new Dictionary<int, decimal?>();
        decimal total = 0, dim = 0;
        foreach (var (secId, qty) in holdings.OrderBy(h => h.Key))
        {
            var sec = f.Securities.Single(x => x.SecurityId == secId);
            var px = f.Prices.SingleOrDefault(p => p.SecurityId == secId && p.PriceDate == asOf);
            var fx = f.FxRates.SingleOrDefault(r => r.Currency == sec.Currency && r.RateDate == asOf);
            if (px is null) { missing.Add($"no close for {sec.Symbol} on {asOf:yyyy-MM-dd}"); values[secId] = null; continue; }
            if (fx is null) { missing.Add($"no {sec.Currency}/USD rate on {asOf:yyyy-MM-dd}"); values[secId] = null; continue; }
            var mv = qty * px.ClosePrice * fx.UsdPerUnit;
            values[secId] = mv;
            total += mv;
            if (inDim.Contains(secId)) dim += mv;
        }
        var targets = FlattenTargets(a.ModelId, 1m);
        var target = targets.Where(t => inDim.Contains(t.Key)).Sum(t => t.Value);
        return new(a, total, dim, target, missing, holdings, values, targets);
    }

    ExpectedCase Drift(EvalCase c, CaseSpec s)
    {
        var asOf = DateOnly.ParseExact(s.AsOf!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var inDim = SecuritiesInDimension(s.DimensionType!, s.DimensionValue!);
        var perAccount = FilterAccounts(c.TenantId, s).Select(a => Value(a, asOf, s.Basis, inDim)).ToList();
        var rows = new List<ExpectedRow>();
        var detail = new List<ExpectedRow>();

        if (s.GroupBy.SequenceEqual(["household"]))
        {
            foreach (var g in perAccount.Where(p => p.Account.HouseholdId is not null).GroupBy(p => p.Account.HouseholdId!.Value))
            {
                var name = f.Households.Single(h => h.HouseholdId == g.Key).Name;
                var missing = g.SelectMany(p => p.Missing).ToList();
                var total = g.Sum(p => p.Total);
                bool kept;
                if (missing.Count > 0) { rows.Add(Flag(name, "Incomplete", missing)); kept = true; }
                else if (total == 0) { rows.Add(Flag(name, "No valuation", [])); kept = true; }
                else
                {
                    var weight = g.Sum(p => p.Dim) * 100m / total;
                    var target = g.Sum(p => p.Target * p.Total) * 100m / total;
                    kept = AddIfBreach(rows, name, total, weight, target, s);
                }
                if (!kept) continue;
                foreach (var p in g)
                    detail.Add(new($"{name} | {p.Account.AccountNumber}", p.Status, new Dictionary<string, string?>
                    {
                        ["total_mv_usd"] = p.Status == "Incomplete" ? null : D(p.Total),
                        ["weight_pct"] = p.Status == "Complete" ? D(p.Dim * 100m / p.Total) : null,
                        ["target_pct"] = D(p.Target * 100m),
                    }, null));
            }
            return new(c.Id, c.TenantId, c.Category, "rows", ["household"], Sort(rows), ["household", "account_number"], Sort(detail));
        }

        foreach (var p in perAccount)
        {
            bool kept;
            if (p.Status != "Complete") { rows.Add(Flag(p.Account.AccountNumber, p.Status, p.Missing)); kept = true; }
            else kept = AddIfBreach(rows, p.Account.AccountNumber, p.Total, p.Dim * 100m / p.Total, p.Target * 100m, s);
            if (!kept) continue;
            // Lines behind the row: sleeve positions held, sleeve securities targeted but not held, and any unpriced position.
            var secs = p.Holdings.Keys.Where(k => inDim.Contains(k) || p.Values[k] is null)
                .Concat(p.Targets.Keys.Where(inDim.Contains)).Distinct();
            foreach (var sec in secs)
            {
                var held = p.Holdings.ContainsKey(sec);
                decimal? mv = held ? p.Values[sec] : 0m;
                detail.Add(new($"{p.Account.AccountNumber} | {f.Securities.Single(x => x.SecurityId == sec).Symbol}", "line", new Dictionary<string, string?>
                {
                    ["quantity"] = D(held ? p.Holdings[sec] : 0m),
                    ["market_value_usd"] = mv is null ? null : D(mv.Value),
                    ["contribution_pct"] = p.Status == "Complete" && mv is not null ? D(mv.Value * 100m / p.Total) : null,
                    ["target_pct"] = D(p.Targets.GetValueOrDefault(sec) * 100m),
                }, null));
            }
        }
        return new(c.Id, c.TenantId, c.Category, "rows", ["account_number"], Sort(rows), ["account_number", "symbol"], Sort(detail));
    }

    static List<ExpectedRow> Sort(List<ExpectedRow> rows) => rows.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();

    static ExpectedRow Flag(string key, string status, List<string> missing) =>
        new(key, status, new Dictionary<string, string?>(), missing.Count == 0 ? null : string.Join("; ", missing.Distinct()));

    static bool AddIfBreach(List<ExpectedRow> rows, string key, decimal total, decimal weight, decimal target, CaseSpec s)
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
        if (!breach) return false;
        rows.Add(new(key, "Complete", new Dictionary<string, string?>
        {
            ["total_mv_usd"] = D(total), ["weight_pct"] = D(weight), ["target_pct"] = D(target), ["drift_pts"] = D(drift),
        }, null));
        return true;
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

    // ---------- allocations ----------

    /// <summary>Cutoff: a stated date means the end of that day, never later than the reference time; otherwise the stated timestamp.</summary>
    DateTime Cutoff(CaseSpec s)
    {
        if (s.AsOf is not null)
        {
            var eod = DateOnly.ParseExact(s.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(new TimeOnly(23, 59, 59));
            return eod < _now ? eod : _now;
        }
        return s.AsOfTime is null ? _now : DateTime.ParseExact(s.AsOfTime, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    static DateTime SessionClose(BlockOrder b, Security sec) =>
        b.TradeDate.ToDateTime(sec.Currency == "EUR" ? new TimeOnly(10, 30) : new TimeOnly(15, 0));

    sealed record AllocLine(Allocation Al, BlockOrder B, Security Sec, Account Acct, decimal Executed, decimal Cancelled,
        decimal Expired, decimal Remaining, string Custodian, string Status);

    ExpectedCase Allocations(EvalCase c, CaseSpec s)
    {
        var cutoff = Cutoff(s);
        var accounts = FilterAccounts(c.TenantId, s).ToDictionary(a => a.AccountId);
        var lines = new List<AllocLine>();
        foreach (var al in f.Allocations)
        {
            var b = f.BlockOrders.Single(x => x.BlockId == al.BlockId);
            var sec = f.Securities.Single(x => x.SecurityId == b.SecurityId);
            if (b.TenantId != c.TenantId || !accounts.TryGetValue(al.AccountId, out var acct)) continue;
            if (b.CreatedAt > cutoff) continue;
            if (s.Side is not null && b.Side != s.Side) continue;
            if (s.AssetClass is not null && sec.AssetClass != s.AssetClass) continue;
            if (s.Symbols.Count > 0 && !s.Symbols.Contains(sec.Symbol)) continue;
            var executed = f.Executions.Where(e => e.AllocationId == al.AllocationId && e.ExecutedAt <= cutoff).Sum(e => e.Quantity);
            var cancelled = al.CancelledAt is { } ca && ca <= cutoff ? al.CancelledQuantity : 0m;
            var open = al.AllocatedQuantity - executed - cancelled;
            var expired = b.TimeInForce == "DAY" && cutoff >= SessionClose(b, sec) && open > 0 ? open : 0m;
            var remaining = open - expired;
            if (s.Status != "all" && remaining <= 0) continue;
            var status = remaining > 0 ? (executed > 0 ? "Partially filled" : "Working")
                : expired > 0 ? "Expired" : cancelled > 0 ? "Remainder cancelled" : "Filled";
            lines.Add(new(al, b, sec, acct, executed, cancelled, expired, remaining, f.Custodians.Single(x => x.CustodianId == acct.CustodianId).Name, status));
        }

        // Security and side are always kept: quantities of different securities or directions are never added together.
        string[] order = ["account_number", "custodian", "block_id", "symbol", "side"];
        var groupCols = order.Where(o => s.GroupBy.Contains(o) || o is "symbol" or "side").ToList();
        string KeyOf(AllocLine l) => string.Join(" | ", groupCols.Select(g => g switch
        {
            "account_number" => l.Acct.AccountNumber,
            "custodian" => l.Custodian,
            "block_id" => l.B.BlockId.ToString(CultureInfo.InvariantCulture),
            "symbol" => l.Sec.Symbol,
            "side" => l.B.Side,
            _ => throw new InvalidDataException(g),
        }));

        var rows = lines.GroupBy(KeyOf).Select(g =>
        {
            var statuses = g.Select(x => x.Status).Distinct().ToList();
            return new ExpectedRow(g.Key, "Complete", new Dictionary<string, string?>
            {
                ["allocated_qty"] = D(g.Sum(x => x.Al.AllocatedQuantity)),
                ["executed_qty"] = D(g.Sum(x => x.Executed)),
                ["cancelled_qty"] = D(g.Sum(x => x.Cancelled)),
                ["expired_qty"] = D(g.Sum(x => x.Expired)),
                ["remaining_qty"] = D(g.Sum(x => x.Remaining)),
                ["order_status"] = statuses.Count == 1 ? statuses[0] : "Mixed",
                ["allocation_count"] = g.Count().ToString(CultureInfo.InvariantCulture),
            }, null);
        }).ToList();
        var detail = lines.Select(l => new ExpectedRow(l.Al.AllocationId.ToString(CultureInfo.InvariantCulture), "line", new Dictionary<string, string?>
        {
            ["allocated_qty"] = D(l.Al.AllocatedQuantity), ["executed_qty"] = D(l.Executed), ["cancelled_qty"] = D(l.Cancelled),
            ["expired_qty"] = D(l.Expired), ["remaining_qty"] = D(l.Remaining), ["line_status"] = l.Status,
        }, null)).ToList();
        return new(c.Id, c.TenantId, c.Category, "rows", groupCols, Sort(rows), ["allocation_id"], Sort(detail));
    }

    static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
