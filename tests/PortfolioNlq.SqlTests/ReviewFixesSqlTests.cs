using PortfolioNlq.Interpretation;
using PortfolioNlq.Fixture;
using PortfolioNlq.Cli;
using PortfolioNlq.Sql;

namespace PortfolioNlq.SqlTests;

public class ReviewFixesSqlTests
{
    static readonly FixtureSet F = FixtureSet.Load(Repo.FixtureDir);
    static readonly ReferenceClock Clock = ReferenceClock.Parse("2026-10-07T09:00:00-05:00", "America/Chicago");
    const string A01 = """{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":2},"as_of":{"date":"2026-10-06"}}""";

    static async Task<CompiledQuery> Compile(string json, int tenant = 1)
    {
        var dir = await new FixtureEntityDirectory(F).LoadAsync(new(tenant, "t"), default);
        return SqlCompiler.Compile(Validator.Validate(ModelOutputParser.Parse(json).Output!, dir, Clock).Query!);
    }

    [SqlFact] // review 9
    public async Task Contributing_lines_cover_only_the_accounts_in_the_answer()
    {
        var r = await new ScopedQueryExecutor(Db.Reader!).ExecuteAsync(new(1, "t"), await Compile(A01), default);
        var answer = r.Rows.Select(x => x["account_number"]).ToHashSet();
        Assert.Equal(answer, r.DetailRows.Select(x => x["account_number"]).ToHashSet());
    }

    [SqlFact] // review 10
    public async Task A_cut_in_the_contributing_lines_marks_the_whole_answer_truncated()
    {
        var r = await new ScopedQueryExecutor(Db.Reader!, new ExecutorOptions(RowCap: 5)).ExecuteAsync(new(1, "t"), await Compile(A01), default);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal("truncated", r.Status);
    }

    [SqlFact] // review 14
    public async Task An_incomplete_account_shows_the_position_that_has_no_price()
    {
        var r = await new ScopedQueryExecutor(Db.Reader!).ExecuteAsync(new(2, "t"), await Compile(A01, 2), default);
        Assert.Contains(r.DetailRows, x => x["account_number"] == "HCC-2001" && x["symbol"] == "HCXN" && x["missing_reason"] is not null);
    }
}
