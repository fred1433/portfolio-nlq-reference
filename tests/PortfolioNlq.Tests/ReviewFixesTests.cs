using System.Globalization;
using ClosedXML.Excel;
using PortfolioNlq.Audit;
using PortfolioNlq.Cli;
using PortfolioNlq.Export;
using PortfolioNlq.Fixture;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Reference;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Tests;

/// <summary>Each test here failed before the fix it guards (review of 7 October 2026).</summary>
public class ReviewFixesTests
{
    static ValidationOutcome V(string json, ReferenceClock? clock = null) =>
        Validator.Validate(ModelOutputParser.Parse(json).Output!, TestData.Directory(1), clock ?? TestData.Clock);

    [Fact] // review 2
    public void The_clock_is_read_in_Chicago_before_the_date_is_taken()
    {
        var late = ReferenceClock.Parse("2026-10-07T03:00:00Z", "America/Chicago"); // 22:00 on the 6th in Chicago
        Assert.Equal(new DateOnly(2026, 10, 6), late.Today);
        Assert.Equal(new DateOnly(2026, 10, 5), late.Resolve("yesterday", []));
    }

    [Fact] // review 2
    public void Fills_and_cancellations_happen_inside_the_session_of_their_market()
    {
        var f = TestData.Fixture;
        foreach (var e in f.Executions)
        {
            var sec = f.Securities.Single(s => s.SecurityId == f.BlockOrders.Single(b => b.BlockId == f.Allocations.Single(a => a.AllocationId == e.AllocationId).BlockId).SecurityId);
            var t = TimeOnly.FromDateTime(e.ExecutedAt);
            if (sec.Currency == "USD") Assert.InRange(t, new TimeOnly(8, 30), new TimeOnly(15, 0));
            else Assert.InRange(t, new TimeOnly(2, 0), new TimeOnly(10, 25)); // Helsinki 10:00 to 18:25 local, in Chicago time
        }
        foreach (var a in f.Allocations.Where(a => a.CancelledAt is not null))
            Assert.True(TimeOnly.FromDateTime(a.CancelledAt!.Value) <= new TimeOnly(15, 0));
    }

    [Fact] // review 8
    public void The_euro_security_is_a_euro_area_issuer_settling_two_days_after_trade()
    {
        var f = TestData.Fixture;
        var eur = f.Securities.Single(s => s.Currency == "EUR" && s.AssetClass == "Equity");
        Assert.DoesNotContain(" ASA", eur.Name);
        foreach (var t in f.Transactions.Where(t => t.ExecutionId is not null && (t.SecurityId == eur.SecurityId || f.Securities.Single(s => s.SecurityId == t.SecurityId).Currency == "EUR")))
            Assert.Equal(t.TradeDate.AddDays(2), t.SettleDate);
    }

    [Fact] // review 13
    public void Blocks_carry_a_time_in_force_so_a_working_order_the_next_morning_is_explained()
    {
        var header = File.ReadLines(Path.Combine(Repo.FixtureDir, "block_orders.csv")).First();
        Assert.Contains("time_in_force", header);
        Assert.Contains("good-till-cancelled", PortfolioNlq.Catalog.QueryCatalog.Definitions["open_allocations"]);
    }

    [Fact] // review 4
    public void Today_never_reads_the_order_book_after_the_reference_time()
    {
        var q = V("""{"kind":"query","measure":"open_allocations","as_of":{"relative":"today"}}""").Query!;
        Assert.True(q.AsOfTime <= new DateTime(2026, 10, 7, 9, 0, 0), q.AsOfTime.ToString("O"));
    }

    [Fact] // review 5
    public void Case_rationales_match_the_fixture()
    {
        var a04 = EvalFile.Load(Repo.CasesFile).Cases.Single(c => c.Id == "A04").Why!;
        var acme = TestData.Fixture.Securities.Single(s => s.Symbol == "ACMR").SecurityId;
        var n = TestData.Fixture.SecurityTags.Count(t => t.SecurityId == acme);
        string[] words = ["zero", "one", "two", "three", "four", "five"];
        Assert.Contains($"carries {words[n]} tags", a04);
    }

    [Fact] // review 9
    public void Contributing_lines_are_limited_to_the_accounts_in_the_answer()
    {
        var q = V("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":2}}""").Query!;
        var c = SqlCompiler.Compile(q);
        Assert.Contains("weight_pct - target_pct > @threshold_points", c.DetailSql);
    }

    static string Xlsx(AnswerTrace t)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nlq-review-{Guid.NewGuid():N}.xlsx");
        ExcelExporter.Write(t, path);
        return path;
    }

    [Fact] // review 3
    public void An_allocation_workbook_says_order_book_not_close()
    {
        var q = V("""{"kind":"query","measure":"open_allocations","side":"sell"}""").Query!;
        var path = Xlsx(new AnswerTrace { RunId = "t", CaseId = "B", Question = "q", Interpretation = q, TimeZone = "America/Chicago",
            Columns = ["symbol", "remaining_qty"], Rows = [new() { ["symbol"] = "USLC", ["remaining_qty"] = "150" }] });
        using var wb = new XLWorkbook(path);
        Assert.Equal("Order book as at 2026-10-07 09:00 America/Chicago, quantities in units", wb.Worksheet("Result").Cell(2, 1).GetString());
    }

    [Fact] // review 14
    public void Share_quantities_are_whole_numbers_in_the_workbook()
    {
        var path = Xlsx(new AnswerTrace { RunId = "t", CaseId = "A", Question = "q",
            Columns = ["account_number"], Rows = [new() { ["account_number"] = "LWP-1001" }],
            DetailRows = [new() { ["account_number"] = "LWP-1001", ["quantity"] = "6900" }, new() { ["account_number"] = "LWP-1001", ["quantity"] = "144210.6" }] });
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Contributing lines");
        Assert.Equal("#,##0", ws.Cell(2, 2).Style.NumberFormat.Format);
        Assert.Equal("#,##0.00", ws.Cell(3, 2).Style.NumberFormat.Format);
    }
}
