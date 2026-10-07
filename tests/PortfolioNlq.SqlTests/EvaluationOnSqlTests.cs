using System.Text.Json;
using PortfolioNlq.Audit;
using PortfolioNlq.Cli;
using PortfolioNlq.Evaluation;
using PortfolioNlq.Fixture;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.SqlTests;

public class EvaluationOnSqlTests
{
    static readonly FixtureSet Base = FixtureSet.Load(Repo.FixtureDir);
    static readonly ReferenceClock Clock = ReferenceClock.Parse("2026-10-07T09:00:00-05:00", "America/Chicago");

    const string A01 = """{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":2},"as_of":{"date":"2026-10-06"}}""";
    const string A04 = """{"kind":"query","measure":"drift","dimension":{"type":"tag","value":"Technology"},"threshold":{"direction":"above","points":1},"as_of":{"date":"2026-10-06"}}""";
    const string B01 = """{"kind":"query","measure":"open_allocations","side":"sell","asset_class":"Equity","group_by":["security","custodian"],"filters":[{"field":"account","value":"Whitfield Family Trust"}]}""";

    static async Task<(ResolvedQuery Query, ExecutionResult Result)> Run(string reader, FixtureSet f, string json, int tenant = 1, Func<string, string>? mutate = null)
    {
        var dir = await new FixtureEntityDirectory(f).LoadAsync(new(tenant, "t"), default);
        var q = Validator.Validate(ModelOutputParser.Parse(json).Output!, dir, Clock).Query!;
        var c = SqlCompiler.Compile(q);
        if (mutate is not null) c = c with { Sql = mutate(c.Sql), DetailSql = c.DetailSql is null ? null : mutate(c.DetailSql) };
        var r = await new ScopedQueryExecutor(reader).ExecuteAsync(new(tenant, "t"), c, default);
        Assert.Equal("complete", r.Status);
        return (q, r);
    }

    static string Rows(ExecutionResult r) => JsonSerializer.Serialize(r.Rows);

    static async Task<string> Copy(string name, FixtureSet f)
    {
        await DbSetup.RunAsync(Db.Admin!, name, Db.ReaderLogin, Db.ReaderPassword, f);
        return Db.ReaderOn(name);
    }

    [SqlFact]
    public async Task Perturbations_that_must_not_move_an_answer_do_not_move_it_on_sql_server()
    {
        var baseA01 = Rows((await Run(Db.Reader!, Base, A01)).Result);
        var baseA04 = Rows((await Run(Db.Reader!, Base, A04)).Result);
        var baseB01 = Rows((await Run(Db.Reader!, Base, B01)).Result);

        // 1. one more tag on a security
        var tagged = Base with { SecurityTags = [.. Base.SecurityTags, new SecurityTag(1, "ESG Screened")] };
        var db = await Copy("NlqPerturbTag", tagged);
        Assert.Equal(baseA01, Rows((await Run(db, tagged, A01)).Result));
        Assert.Equal(baseA04, Rows((await Run(db, tagged, A04)).Result));

        // 2. one fill split in two (execution and its booked trade and cash)
        var e = Base.Executions.Single(x => x.ExecutionId == 2);
        var trade = Base.Transactions.Single(t => t.ExecutionId == 2 && !t.Type.StartsWith("Cash", StringComparison.Ordinal));
        var cash = Base.Transactions.Single(t => t.ExecutionId == 2 && t.Type.StartsWith("Cash", StringComparison.Ordinal));
        var split = Base with
        {
            Executions = [.. Base.Executions.Where(x => x.ExecutionId != 2), e with { Quantity = 120 }, e with { ExecutionId = 99, Quantity = 80 }],
            Transactions =
            [
                .. Base.Transactions.Where(t => t.ExecutionId != 2),
                trade with { Quantity = -120 }, cash with { Quantity = 120 * e.Price },
                trade with { TransactionId = 990, Quantity = -80, ExecutionId = 99 }, cash with { TransactionId = 991, Quantity = 80 * e.Price, ExecutionId = 99 },
            ],
        };
        db = await Copy("NlqPerturbSplit", split);
        Assert.Equal(baseB01, Rows((await Run(db, split, B01)).Result));
        Assert.Equal(baseA01, Rows((await Run(db, split, A01)).Result));

        // 3. another tenant's activity
        var other = Base with { Allocations = [.. Base.Allocations, new Allocation(9999, 905, 203, 75, 0, null)] };
        db = await Copy("NlqPerturbOther", other);
        Assert.Equal(baseB01, Rows((await Run(db, other, B01)).Result));
        Assert.Equal(baseA01, Rows((await Run(db, other, A01)).Result));
    }

    /// <summary>
    /// Injected defect, named as such: the threshold written as >= instead of >. Okafor Roth IRA sits at exactly +2.00,
    /// so the evaluation must flag an extra row. Proves the grading would catch this class of mistake.
    /// </summary>
    [SqlFact]
    public async Task Injected_defect_greater_or_equal_threshold_is_caught_by_the_evaluation()
    {
        Grade GradeOf(ResolvedQuery q, ExecutionResult r) => Commands.GradeOne("A01", new AnswerTrace
        {
            CaseId = "A01", TenantId = 1, Outcome = "answered", Interpretation = q, ExecutionStatus = r.Status,
            Columns = r.Columns.ToList(), Rows = r.Rows.ToList(), DetailRows = r.DetailRows.ToList(),
        }, Base);

        var (q, good) = await Run(Db.Reader!, Base, A01);
        Assert.True(GradeOf(q, good).Passed);

        var (_, bad) = await Run(Db.Reader!, Base, A01, mutate: sql => sql.Replace("weight_pct - target_pct > @threshold_points", "weight_pct - target_pct >= @threshold_points"));
        var g = GradeOf(q, bad);
        Assert.False(g.Passed);
        Assert.Equal("wrong", g.Verdict);
        Assert.Contains("LWP-1004", g.Summary);
    }
}
