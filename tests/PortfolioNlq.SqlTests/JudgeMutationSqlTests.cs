using Microsoft.Data.SqlClient;
using PortfolioNlq.Cli;
using PortfolioNlq.Fixture;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.SqlTests;

public class JudgeMutationSqlTests
{
    static readonly FixtureSet Base = FixtureSet.Load(Repo.FixtureDir);
    static readonly ReferenceClock Clock = ReferenceClock.Parse("2026-10-07T09:00:00-05:00", "America/Chicago");
    const string A01 = """{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":2},"as_of":{"date":"2026-10-06"}}""";
    const string B01 = """{"kind":"query","measure":"open_allocations","side":"sell","asset_class":"Equity","group_by":["security","custodian"],"filters":[{"field":"account","value":"Whitfield Family Trust"}]}""";
    const string A05 = """{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"either","points":2},"group_by":["household"],"as_of":{"date":"2026-10-06"}}""";

    static async Task<ExecutionResult> Run(string reader, FixtureSet f, string json, int tenant = 1)
    {
        var dir = await new FixtureEntityDirectory(f).LoadAsync(new(tenant, "t"), default);
        var q = Validator.Validate(ModelOutputParser.Parse(json).Output!, dir, Clock).Query!;
        return await new ScopedQueryExecutor(reader).ExecuteAsync(new(tenant, "t"), SqlCompiler.Compile(q), default);
    }

    static async Task<string> Copy(string name, FixtureSet f)
    {
        await DbSetup.RunAsync(Db.Admin!, name, Db.ReaderLogin, Db.ReaderPassword, f);
        return Db.ReaderOn(name);
    }

    [SqlFact] // judge 3: DAY expiry changes the open state in SQL too
    public async Task A_day_order_remainder_is_not_open_the_next_morning_on_sql_server()
    {
        var f = Base with { BlockOrders = Base.BlockOrders.Select(b => b.BlockId == 902 ? b with { TimeInForce = "DAY" } : b).ToList() };
        var r = await Run(await Copy("NlqJudgeDay", f), f, B01);
        Assert.DoesNotContain(r.Rows, x => x["symbol"] == "ACMR");
    }

    [SqlFact] // judge 7: an account with nothing to value says so instead of vanishing or reading Complete
    public async Task An_empty_account_is_reported_as_no_valuation()
    {
        var f = Base with { Accounts = [.. Base.Accounts, new Account(107, 1, "LWP-1007", "Empty Test Account", "Advisory", 1, 1, 1, "USD")] };
        var r = await Run(await Copy("NlqJudgeEmpty", f), f, A01);
        Assert.Contains(r.Rows, x => x["account_number"] == "LWP-1007" && x["row_status"] == "No valuation");
    }

    [SqlFact] // judge 7: a security in the model but not held still appears in the contributing lines
    public async Task Target_only_securities_appear_in_the_contributing_lines()
    {
        var f = Base with { ModelComponents = Base.ModelComponents.Select(c => c.ModelId == 3 && c.SecurityId == 1 ? c with { Weight = 0.50m } : c).Append(new ModelComponent(3, null, 5, 0.05m)).ToList() };
        var r = await Run(await Copy("NlqJudgeTargetOnly", f), f, A01);
        Assert.Contains(r.DetailRows, x => x["account_number"] == "LWP-1001" && x["symbol"] == "VNTB" && x["quantity"] == "0");
    }

    [SqlFact] // judge 7: the household answer exposes its component accounts
    public async Task A_household_answer_lists_its_component_accounts()
    {
        var r = await Run(Db.Reader!, Base, A05);
        Assert.Contains(r.DetailRows, x => x.GetValueOrDefault("household") == "Whitfield Family" && x.GetValueOrDefault("account_number") == "LWP-1002");
    }

    [SqlFact] // judge 4: a cancellation between open and scope setup closes the connection
    public async Task Cancelling_during_scope_setup_closes_the_connection()
    {
        SqlConnection? seen = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionScope.OpenAsync(Db.Reader!, new(1, "t"), default,
            (c, _) => { seen = c; throw new OperationCanceledException(); }));
        Assert.NotNull(seen);
        Assert.Equal(System.Data.ConnectionState.Closed, seen!.State);
    }
}
