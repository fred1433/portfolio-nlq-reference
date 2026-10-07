using System.Globalization;
using System.Text;
using PortfolioNlq.Catalog;
using PortfolioNlq.Interpretation;

namespace PortfolioNlq.Sql;

public sealed record QueryParameter(string Name, string SqlType, string Value);

public sealed record CompiledQuery(string Sql, IReadOnlyList<QueryParameter> Parameters, string? DetailSql, IReadOnlyList<string> KeyColumns);

/// <summary>
/// Turns a resolved query into parameterised SQL over the rpt views. Every formula and join below was written
/// and reviewed by hand; the only variable parts are which reviewed fragments are included and the parameter values.
/// No text coming from the user or the model is ever concatenated into SQL.
/// </summary>
public static class SqlCompiler
{
    public const string Version = "compiler-2026.10.4";

    public static CompiledQuery Compile(ResolvedQuery q) => q.Measure switch
    {
        QueryCatalog.Drift => q.GroupBy[0] == "household" ? DriftByHousehold(q) : DriftByAccount(q),
        QueryCatalog.OpenAllocations => Allocations(q),
        _ => throw new InvalidOperationException("measure " + q.Measure),
    };

    static string ThresholdClause(ResolvedQuery q, string drift) => q.ThresholdDirection switch
    {
        null => "1 = 1",
        "above" => $"{drift} > @threshold_points",
        "below" => $"{drift} < -@threshold_points",
        "either" => $"ABS({drift}) > @threshold_points",
        _ => throw new InvalidOperationException(q.ThresholdDirection),
    };

    static List<QueryParameter> DriftParameters(ResolvedQuery q)
    {
        var p = new List<QueryParameter>
        {
            new("@as_of", "date", q.AsOf!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("@basis", "varchar(10)", q.DateBasis),
            new("@dimension_value", "nvarchar(60)", q.DimensionValue!),
        };
        if (q.ThresholdPoints is decimal t) p.Add(new("@threshold_points", "decimal(9,4)", t.ToString(CultureInfo.InvariantCulture)));
        return p;
    }

    static string DimensionCte(ResolvedQuery q) => q.DimensionType == "tag"
        ? "    SELECT t.security_id FROM rpt.v_security_tags AS t WHERE t.tag = @dimension_value"
        : "    SELECT s.security_id FROM rpt.v_securities AS s WHERE s.asset_class = @dimension_value";

    static string AccountFilters(ResolvedQuery q, List<QueryParameter> p, string alias)
    {
        var sb = new StringBuilder();
        foreach (var f in q.Filters)
        {
            var column = f.Field switch
            {
                "account" => "account_id", "household" => "household_id", "custodian" => "custodian_id",
                "model" => "model_id", "security" => "security_id", _ => throw new InvalidOperationException(f.Field),
            };
            var names = new List<string>();
            for (var i = 0; i < f.Ids.Count; i++)
            {
                var name = $"@{f.Field}_{p.Count(x => x.Name.StartsWith("@" + f.Field + "_", StringComparison.Ordinal))}";
                p.Add(new(name, "int", f.Ids[i].ToString(CultureInfo.InvariantCulture)));
                names.Add(name);
            }
            sb.Append($"\n      AND {alias}.{column} IN ({string.Join(", ", names)})");
        }
        return sb.ToString();
    }

    // Row status: Incomplete (a position has no close or no FX rate), No valuation (nothing to value), else Complete.
    const string StatusExpr = "CASE WHEN missing_detail IS NOT NULL THEN 'Incomplete' WHEN COALESCE(total_mv_usd, 0) = 0 THEN 'No valuation' ELSE 'Complete' END";

    static CompiledQuery DriftByAccount(ResolvedQuery q)
    {
        var p = DriftParameters(q);
        var filters = AccountFilters(q, p, "a");
        var keep = ThresholdClause(q, "weight_pct - target_pct");
        var ctes = $"""
WITH dim AS (
{DimensionCte(q)}
), acct AS (
    SELECT a.account_id, a.account_number, a.account_name, a.household, a.custodian, a.model
    FROM rpt.v_accounts AS a
    WHERE 1 = 1{filters}
), pos AS (
    SELECT p.account_id, p.security_id, p.market_value_usd, p.missing_reason
    FROM rpt.fn_positions(@as_of, @basis) AS p
), tgt AS (
    SELECT t.account_id, SUM(t.target_weight) AS target_weight
    FROM rpt.fn_account_targets() AS t
    WHERE t.security_id IN (SELECT security_id FROM dim)
    GROUP BY t.account_id
), agg AS (
    SELECT acct.account_id,
           SUM(pos.market_value_usd) AS total_mv_usd,
           SUM(CASE WHEN dim.security_id IS NOT NULL THEN pos.market_value_usd ELSE 0 END) AS dim_mv_usd,
           STRING_AGG(pos.missing_reason, N'; ') WITHIN GROUP (ORDER BY pos.missing_reason) AS missing_detail
    FROM acct
    LEFT JOIN pos ON pos.account_id = acct.account_id
    LEFT JOIN dim ON dim.security_id = pos.security_id
    GROUP BY acct.account_id
), valued AS (
    SELECT acct.account_id, acct.account_number, acct.account_name, acct.household, acct.custodian, acct.model,
           agg.total_mv_usd, agg.dim_mv_usd, agg.missing_detail, tgt.target_weight
    FROM acct
    JOIN agg ON agg.account_id = acct.account_id
    LEFT JOIN tgt ON tgt.account_id = acct.account_id
), scored AS (
    SELECT account_id, account_number, account_name, household, custodian, model, total_mv_usd, missing_detail,
           {StatusExpr} AS row_status,
           CASE WHEN missing_detail IS NULL AND COALESCE(total_mv_usd, 0) <> 0
                THEN CAST(dim_mv_usd AS decimal(20,6)) * 100 / CAST(total_mv_usd AS decimal(20,6)) END AS weight_pct,
           CAST(COALESCE(target_weight, 0) * 100 AS decimal(20,10)) AS target_pct
    FROM valued
)
""";
        var sql = ctes + $"""
SELECT account_number, account_name, household, custodian, model,
       CASE WHEN row_status = 'Complete' THEN total_mv_usd END AS total_mv_usd,
       weight_pct, target_pct, weight_pct - target_pct AS drift_pts,
       row_status, missing_detail
FROM scored
WHERE row_status <> 'Complete' OR {keep}
ORDER BY CASE WHEN row_status = 'Complete' THEN 0 ELSE 1 END, weight_pct - target_pct DESC, account_number;
""";
        // Lines behind the answer only: for each account in the result, the sleeve positions held, the sleeve securities
        // its model targets but it does not hold, and any position without a close or an FX rate.
        var detail = ctes + $"""
, kept AS (
    SELECT account_id, account_number, row_status FROM scored WHERE row_status <> 'Complete' OR {keep}
), tgt_sec AS (
    SELECT t.account_id, t.security_id, t.target_weight FROM rpt.fn_account_targets() AS t
), held AS (
    SELECT p.* FROM rpt.fn_positions(@as_of, @basis) AS p JOIN kept ON kept.account_id = p.account_id
), lines AS (
    SELECT account_id, security_id FROM held WHERE security_id IN (SELECT security_id FROM dim) OR missing_reason IS NOT NULL
    UNION
    SELECT t.account_id, t.security_id FROM tgt_sec AS t JOIN kept ON kept.account_id = t.account_id
    WHERE t.security_id IN (SELECT security_id FROM dim)
), tot AS (
    SELECT account_id, SUM(market_value_usd) AS total_mv_usd FROM held GROUP BY account_id
)
SELECT kept.account_number, s.symbol, s.security_name, COALESCE(h.quantity, 0) AS quantity, s.currency, h.close_price, h.usd_per_unit,
       CASE WHEN h.security_id IS NULL THEN 0 ELSE h.market_value_usd END AS market_value_usd,
       CASE WHEN kept.row_status = 'Complete'
            THEN CASE WHEN h.security_id IS NULL THEN 0
                      ELSE CAST(h.market_value_usd AS decimal(20,6)) * 100 / CAST(tot.total_mv_usd AS decimal(20,6)) END END AS contribution_pct,
       CAST(COALESCE(ts.target_weight, 0) * 100 AS decimal(20,10)) AS target_pct,
       h.missing_reason
FROM lines AS l
JOIN kept ON kept.account_id = l.account_id
JOIN rpt.v_securities AS s ON s.security_id = l.security_id
LEFT JOIN held AS h ON h.account_id = l.account_id AND h.security_id = l.security_id
LEFT JOIN tot ON tot.account_id = l.account_id
LEFT JOIN tgt_sec AS ts ON ts.account_id = l.account_id AND ts.security_id = l.security_id
ORDER BY kept.account_number, CASE WHEN h.security_id IS NULL THEN 0 ELSE h.market_value_usd END DESC, s.symbol;
""";
        return new(sql, p, detail, ["account_number"]);
    }

    static CompiledQuery DriftByHousehold(ResolvedQuery q)
    {
        var p = DriftParameters(q);
        var filters = AccountFilters(q, p, "a");
        var keep = ThresholdClause(q, "weight_pct - target_pct");
        var ctes = $"""
WITH dim AS (
{DimensionCte(q)}
), acct AS (
    SELECT a.account_id, a.account_number, a.household_id, a.household
    FROM rpt.v_accounts AS a
    WHERE a.household_id IS NOT NULL{filters}
), pos AS (
    SELECT p.account_id, p.security_id, p.market_value_usd, p.missing_reason
    FROM rpt.fn_positions(@as_of, @basis) AS p
), tgt AS (
    SELECT t.account_id, SUM(t.target_weight) AS target_weight
    FROM rpt.fn_account_targets() AS t
    WHERE t.security_id IN (SELECT security_id FROM dim)
    GROUP BY t.account_id
), per_account AS (
    SELECT acct.account_id, acct.account_number, acct.household_id, acct.household,
           SUM(pos.market_value_usd) AS total_mv_usd,
           SUM(CASE WHEN dim.security_id IS NOT NULL THEN pos.market_value_usd ELSE 0 END) AS dim_mv_usd,
           STRING_AGG(pos.missing_reason, N'; ') WITHIN GROUP (ORDER BY pos.missing_reason) AS missing_detail
    FROM acct
    LEFT JOIN pos ON pos.account_id = acct.account_id
    LEFT JOIN dim ON dim.security_id = pos.security_id
    GROUP BY acct.account_id, acct.account_number, acct.household_id, acct.household
), hh AS (
    SELECT pa.household,
           COUNT(*) AS account_count,
           SUM(pa.total_mv_usd) AS total_mv_usd,
           SUM(pa.dim_mv_usd) AS dim_mv_usd,
           SUM(COALESCE(tgt.target_weight, 0) * pa.total_mv_usd) AS target_mv_usd,
           STRING_AGG(pa.missing_detail, N'; ') AS missing_detail
    FROM per_account AS pa
    LEFT JOIN tgt ON tgt.account_id = pa.account_id
    GROUP BY pa.household_id, pa.household
), scored AS (
    SELECT household, account_count, total_mv_usd, missing_detail,
           {StatusExpr} AS row_status,
           CASE WHEN missing_detail IS NULL AND COALESCE(total_mv_usd, 0) <> 0
                THEN CAST(dim_mv_usd AS decimal(20,6)) * 100 / CAST(total_mv_usd AS decimal(20,6)) END AS weight_pct,
           CASE WHEN missing_detail IS NULL AND COALESCE(total_mv_usd, 0) <> 0
                THEN CAST(target_mv_usd AS decimal(24,10)) * 100 / CAST(total_mv_usd AS decimal(20,6)) END AS target_pct
    FROM hh
)
""";
        var sql = ctes + $"""
SELECT household, account_count,
       CASE WHEN row_status = 'Complete' THEN total_mv_usd END AS total_mv_usd,
       weight_pct, target_pct, weight_pct - target_pct AS drift_pts,
       row_status, missing_detail
FROM scored
WHERE row_status <> 'Complete' OR {keep}
ORDER BY CASE WHEN row_status = 'Complete' THEN 0 ELSE 1 END, weight_pct - target_pct DESC, household;
""";
        // The component accounts of each household in the result: value, sleeve weight, model target.
        var detail = ctes + $"""
SELECT pa.household, pa.account_number,
       CASE WHEN pa.missing_detail IS NULL THEN COALESCE(pa.total_mv_usd, 0) END AS total_mv_usd,
       CASE WHEN pa.missing_detail IS NULL AND COALESCE(pa.total_mv_usd, 0) <> 0
            THEN CAST(pa.dim_mv_usd AS decimal(20,6)) * 100 / CAST(pa.total_mv_usd AS decimal(20,6)) END AS weight_pct,
       CAST(COALESCE(tgt.target_weight, 0) * 100 AS decimal(20,10)) AS target_pct
FROM per_account AS pa
LEFT JOIN tgt ON tgt.account_id = pa.account_id
WHERE pa.household IN (SELECT household FROM scored WHERE row_status <> 'Complete' OR {keep})
ORDER BY pa.household, pa.account_number;
""";
        return new(sql, p, detail, ["household"]);
    }

    // Security and side are always kept in the rows: quantities of different securities or directions are never added.
    static readonly (string Group, string[] Columns, string Key)[] AllocationGroups =
    [
        ("account", ["account_number", "account_name"], "account_number"),
        ("custodian", ["custodian"], "custodian"),
        ("block", ["block_id"], "block_id"),
        ("security", ["symbol", "security_name"], "symbol"),
        ("side", ["side"], "side"),
    ];

    static CompiledQuery Allocations(ResolvedQuery q)
    {
        var p = new List<QueryParameter> { new("@as_of_time", "datetime2(0)", q.AsOfTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)) };
        var where = new StringBuilder();
        if (q.Side is not null) { p.Add(new("@side", "varchar(4)", q.Side)); where.Append("\n      AND l.side = @side"); }
        if (q.AssetClass is not null) { p.Add(new("@asset_class", "nvarchar(40)", q.AssetClass)); where.Append("\n      AND l.asset_class = @asset_class"); }
        where.Append(AccountFilters(q, p, "l"));
        var groups = AllocationGroups.Where(g => q.GroupBy.Contains(g.Group) || g.Group is "security" or "side").ToList();
        var cols = string.Join(", ", groups.SelectMany(g => g.Columns));
        var status = q.Status == "open" ? "remaining_qty > 0" : "1 = 1";
        var ctes = $"""
WITH ex AS (
    SELECT e.allocation_id, SUM(e.quantity) AS executed_qty
    FROM rpt.v_executions AS e
    WHERE e.executed_at <= @as_of_time
    GROUP BY e.allocation_id
), lines AS (
    SELECT l.allocation_id, l.block_id, l.side, l.time_in_force, l.expires_at, l.symbol, l.security_name,
           l.account_number, l.account_name, l.custodian,
           l.allocated_quantity,
           COALESCE(ex.executed_qty, 0) AS executed_qty,
           CASE WHEN l.cancelled_at <= @as_of_time THEN l.cancelled_quantity ELSE 0 END AS cancelled_qty
    FROM rpt.v_allocation_lines AS l
    LEFT JOIN ex ON ex.allocation_id = l.allocation_id
    WHERE l.created_at <= @as_of_time{where}
), expiry AS (
    SELECT lines.*,
           CASE WHEN expires_at IS NOT NULL AND @as_of_time >= expires_at AND allocated_quantity - executed_qty - cancelled_qty > 0
                THEN allocated_quantity - executed_qty - cancelled_qty ELSE 0 END AS expired_qty
    FROM lines
), scored AS (
    SELECT expiry.*,
           allocated_quantity - executed_qty - cancelled_qty - expired_qty AS remaining_qty,
           CASE WHEN allocated_quantity - executed_qty - cancelled_qty - expired_qty > 0
                THEN CASE WHEN executed_qty > 0 THEN N'Partially filled' ELSE N'Working' END
                WHEN expired_qty > 0 THEN N'Expired'
                WHEN cancelled_qty > 0 THEN N'Remainder cancelled'
                ELSE N'Filled' END AS line_status
    FROM expiry
)
""";
        var sql = ctes + $"""
SELECT {cols},
       SUM(allocated_quantity) AS allocated_qty,
       SUM(executed_qty) AS executed_qty,
       SUM(cancelled_qty) AS cancelled_qty,
       SUM(expired_qty) AS expired_qty,
       SUM(remaining_qty) AS remaining_qty,
       CASE WHEN MIN(line_status) = MAX(line_status) THEN MIN(line_status) ELSE N'Mixed' END AS order_status,
       COUNT(*) AS allocation_count
FROM scored
WHERE {status}
GROUP BY {cols}
ORDER BY {cols};
""";
        var detail = ctes + $"""
SELECT allocation_id, block_id, side, symbol, account_number, custodian, time_in_force,
       allocated_quantity AS allocated_qty, executed_qty, cancelled_qty, expired_qty, remaining_qty, line_status
FROM scored
WHERE {status}
ORDER BY account_number, symbol, allocation_id;
""";
        return new(sql, p, detail, groups.Select(g => g.Key).ToList());
    }
}
