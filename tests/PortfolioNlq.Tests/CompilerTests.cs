using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Tests;

public class CompilerTests
{
    static CompiledQuery C(string json, int tenant = 1) =>
        SqlCompiler.Compile(Validator.Validate(ModelOutputParser.Parse(json).Output!, TestData.Directory(tenant), TestData.Clock).Query!);

    [Fact]
    public void Names_and_values_travel_as_parameters_never_as_sql_text()
    {
        var q = C("""{"kind":"query","measure":"drift","dimension":{"type":"tag","value":"Technology"},"threshold":{"direction":"above","points":1},"filters":[{"field":"custodian","value":"Schwab"}]}""");
        Assert.DoesNotContain("Technology", q.Sql);
        Assert.DoesNotContain("Schwab", q.Sql);
        Assert.Contains(q.Parameters, p => p.Name == "@dimension_value" && p.Value == "Technology");
        Assert.Contains(q.Parameters, p => p.Name == "@custodian_0" && p.Value == "1");
    }

    [Fact]
    public void The_threshold_is_strict_so_an_account_at_exactly_two_points_is_not_above_two()
    {
        var q = C("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":2}}""");
        Assert.Contains("weight_pct - target_pct > @threshold_points", q.Sql);
        Assert.DoesNotContain(">= @threshold_points", q.Sql);
    }

    [Fact]
    public void Incomplete_accounts_are_kept_in_the_result_never_filtered_out_or_zeroed()
    {
        var q = C("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":2}}""");
        Assert.Contains("WHERE row_status <> 'Complete' OR", q.Sql);
        Assert.Contains("CASE WHEN missing_detail IS NULL AND COALESCE(total_mv_usd, 0) <> 0", q.Sql);
    }

    [Fact]
    public void Run_one_defect_stays_fixed_no_subquery_inside_an_aggregate()
    {
        foreach (var json in new[]
        {
            """{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"}}""",
            """{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"group_by":["household"]}""",
        })
            Assert.DoesNotContain("SUM(CASE WHEN pos.security_id IN (SELECT", C(json).Sql);
    }

    [Fact]
    public void Allocation_groups_follow_the_request()
    {
        var q = C("""{"kind":"query","measure":"open_allocations","side":"sell","group_by":["security","custodian"],"filters":[{"field":"account","value":"Whitfield Family Trust"}]}""");
        Assert.Equal(["custodian", "symbol", "side"], q.KeyColumns); // security and side always kept
        Assert.Contains("GROUP BY custodian, symbol, security_name, side", q.Sql);
        Assert.Contains(q.Parameters, p => p.Name == "@side" && p.Value == "Sell");
    }
}
