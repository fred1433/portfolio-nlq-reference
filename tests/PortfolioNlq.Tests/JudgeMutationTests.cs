using System.Text.Json;
using PortfolioNlq.Audit;
using PortfolioNlq.Cli;
using PortfolioNlq.Evaluation;
using PortfolioNlq.Fixture;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Reference;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Tests;

/// <summary>The mutations of the 7 October external review, reproduced. Each test failed before its fix.</summary>
public class JudgeMutationTests
{
    static string PublishedRun => Path.Combine(Repo.Root, File.ReadAllText(Path.Combine(Repo.Root, "runs", "PUBLISHED")).Trim().Split(' ')[1]);
    static string PublishedRecording => Path.Combine(Repo.Root, File.ReadAllText(Path.Combine(Repo.Root, "runs", "PUBLISHED")).Trim().Split(' ')[0]);
    static AnswerTrace Trace(string id) => AnswerTrace.Load(Path.Combine(PublishedRun, "traces", id + ".json"));
    static ExpectedCaseDto Expected(string id) => ExpectedFile.Load(Repo.ExpectedFile).Cases.Single(c => c.CaseId == id);
    static Grade G(string id, AnswerTrace t) => Commands.GradeOne(id, t, TestData.Fixture);

    [Fact] // judge 2: a threshold of 2.5 returns the same three rows and must still fail
    public void A_wrong_threshold_with_identical_rows_is_graded_wrong()
    {
        var t = Trace("A01");
        t.Interpretation = t.Interpretation! with { ThresholdPoints = 2.5m };
        Assert.False(G("A01", t).Passed);
    }

    [Theory] // judge 2: an operational error is not a held authorization case
    [InlineData("X01")] [InlineData("X03")] [InlineData("X04")] [InlineData("X05")]
    public void An_error_trace_does_not_pass_an_authorization_case(string id)
    {
        var t = Trace(id);
        t.Outcome = "error"; t.ExecutionStatus = "error"; t.Rows = [];
        Assert.False(G(id, t).Passed);
    }

    [Fact] // judge 2: a corrupted contributing value must turn the case red
    public void A_corrupted_contributing_value_fails_the_case()
    {
        var t = Trace("A01");
        t.DetailRows[0]["market_value_usd"] = "999999999999";
        Assert.False(G("A01", t).Passed);
    }

    [Fact] // judge 2: verify must catch a tampered contributing line in the stored run
    public async Task Verify_catches_a_tampered_contributing_line()
    {
        var copy = Directory.CreateTempSubdirectory("nlq-verify-").FullName;
        foreach (var f in Directory.GetFiles(PublishedRun, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(copy, Path.GetRelativePath(PublishedRun, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        var path = Path.Combine(copy, "traces", "A01.json");
        var t = AnswerTrace.Load(path);
        t.DetailRows[0]["market_value_usd"] = "999999999999";
        File.WriteAllText(path, t.ToJson());
        Assert.NotEqual(0, await Commands.Verify(PublishedRecording, copy));
    }

    [Fact] // judge 3: the reference must apply block creation time; nothing existed on 2 October
    public void The_reference_order_book_on_2_October_is_empty()
    {
        var c = EvalFile.Load(Repo.CasesFile).Cases.Single(x => x.Id == "B01");
        c.Spec!.AsOf = "2026-10-02";
        Assert.Empty(new ReferenceCalculator(TestData.Fixture).Compute(c).Rows);
    }

    [Fact] // judge 3: a DAY order expires at the close and is not working the next morning
    public void A_day_order_remainder_is_not_open_the_next_morning()
    {
        var f0 = TestData.Fixture;
        var f = f0 with { BlockOrders = f0.BlockOrders.Select(b => b.BlockId == 902 ? b with { TimeInForce = "DAY" } : b).ToList() };
        var c = EvalFile.Load(Repo.CasesFile).Cases.Single(x => x.Id == "B01");
        Assert.DoesNotContain(new ReferenceCalculator(f).Compute(c).Rows, r => r.Key.Contains("ACMR"));
    }

    [Fact] // judge 1: quantities of different securities are never added together
    public void Allocation_rows_never_add_two_securities()
    {
        var c = EvalFile.Load(Repo.CasesFile).Cases.Single(x => x.Id == "B03");
        var rows = new ReferenceCalculator(TestData.Fixture).Compute(c).Rows;
        Assert.DoesNotContain(rows, r => r.Values.GetValueOrDefault("allocated_qty") == "620");
        Assert.All(rows, r => Assert.True(r.Key.Contains("USLC") || r.Key.Contains("ACMR"), r.Key));
    }

    sealed class Fixed(string reply) : ITranslator
    {
        public Task<Translation> TranslateAsync(string caseId, string question, CancellationToken ct) =>
            Task.FromResult(new Translation(reply, new ModelCall { RawResponse = reply }));
    }
    sealed class Failing : ITranslator
    {
        public Task<Translation> TranslateAsync(string caseId, string question, CancellationToken ct) =>
            throw new HttpRequestException("provider unreachable");
    }
    static AnswerPipeline Pipeline(ITranslator tr) => new(tr, new FixtureEntityDirectory(TestData.Fixture), null,
        new PipelineContext("t", TestData.Clock, new FixtureRef(), new Versions(), "test"));

    [Theory] // judge 4: null collection members become a rejection with a trace, never an exception
    [InlineData("""{"kind":"query","measure":"open_allocations","group_by":[null]}""")]
    [InlineData("""{"kind":"query","measure":"open_allocations","filters":[null]}""")]
    public async Task Null_members_are_rejected_with_a_trace(string reply)
    {
        var t = await Pipeline(new Fixed(reply)).AnswerAsync("N", "q", new ScopeIdentity(1, "t"), default);
        Assert.Equal("rejected", t.Outcome);
    }

    [Fact] // judge 4: a provider failure still returns a trace saying where it failed
    public async Task A_provider_failure_returns_a_trace()
    {
        var t = await Pipeline(new Failing()).AnswerAsync("N", "q", new ScopeIdentity(1, "t"), default);
        Assert.Equal("error", t.Outcome);
        Assert.Equal("model", t.OutcomeSource);
    }

    [Fact] // judge 5: one prompt version names one rendered prompt; history is corrected, not rewritten
    public void Every_recording_prompt_hash_is_registered_under_its_own_version()
    {
        var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "recordings", "prompt_versions.json"))).RootElement;
        var versions = reg.GetProperty("versions").EnumerateArray().ToDictionary(v => v.GetProperty("version").GetString()!, v => v.GetProperty("sha256").GetString()!);
        Assert.Equal(versions.Count, versions.Values.Distinct().Count());
        var corrections = reg.GetProperty("corrections").EnumerateArray().ToDictionary(c => c.GetProperty("recording").GetString()!, c => c.GetProperty("actual_sha256").GetString()!);
        foreach (var dir in Directory.GetDirectories(Path.Combine(Repo.Root, "recordings")))
        {
            var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement;
            var id = Path.GetFileName(dir);
            var (ver, sha) = (m.GetProperty("prompt_version").GetString()!, m.GetProperty("prompt_sha256").GetString()!);
            Assert.True(versions.GetValueOrDefault(ver) == sha || corrections.GetValueOrDefault(id) == sha, $"{id}: {ver} {sha[..12]}");
        }
        Assert.Equal(PortfolioNlq.Catalog.PromptBuilder.Sha256(), versions[PortfolioNlq.Catalog.PromptBuilder.Version]);
    }

    [Fact] // judge 6: a threshold finer than four decimals is not silently rounded
    public void A_threshold_with_five_decimals_is_not_accepted_as_is()
    {
        var v = Validator.Validate(ModelOutputParser.Parse("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"threshold":{"direction":"above","points":1.99999}}""").Output!,
            TestData.Directory(1), TestData.Clock);
        Assert.NotEqual(OutcomeKind.Query, v.Kind);
        Assert.ThrowsAny<ArgumentException>(() => ScopedQueryExecutor.ToSqlParameter(new QueryParameter("@threshold_points", "decimal(9,4)", "1.99999")));
    }

    [Fact] // judge 6: whole-unit convention for securities is enforced when the fixture loads
    public void A_fractional_share_quantity_is_refused_by_the_loader()
    {
        var dir = Directory.CreateTempSubdirectory("nlq-fx-").FullName;
        foreach (var f in FixtureSet.Files) File.Copy(Path.Combine(Repo.FixtureDir, f), Path.Combine(dir, f));
        var p = Path.Combine(dir, "allocations.csv");
        File.WriteAllText(p, File.ReadAllText(p).Replace("9011,901,101,500,", "9011,901,101,500.5,"));
        Assert.Throws<InvalidDataException>(() => FixtureSet.Load(dir));
    }
}
