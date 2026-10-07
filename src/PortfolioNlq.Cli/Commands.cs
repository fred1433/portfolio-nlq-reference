using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PortfolioNlq.Audit;
using PortfolioNlq.Evaluation;
using PortfolioNlq.Fixture;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Reference;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Cli;

public sealed class RunSummary
{
    public string RunId { get; set; } = "";
    public string RecordingId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string ExecutedOn { get; set; } = "";
    public string ExecutedAt { get; set; } = "";
    public string FixtureId { get; set; } = "";
    public string FixtureSha256 { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ComparisonRule { get; set; } = Grader.Rule;
    public List<Grade> Grades { get; set; } = [];
    public Dictionary<string, Dictionary<string, int>> ByCategory { get; set; } = [];
    public Dictionary<string, string> TraceSha256 { get; set; } = [];
}

public static class Commands
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------- expected ----------
    public static int Expected()
    {
        var f = Repo.Fixture();
        var eval = EvalFile.Load(Repo.CasesFile);
        var calc = new ReferenceCalculator(f);
        var file = new
        {
            fixture_sha256 = f.Sha256,
            cases_sha256 = Repo.FileSha256(Repo.CasesFile),
            computed_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            generator = "PortfolioNlq.Reference (LINQ over fixture/*.csv; does not use the views, the catalog or the compiler)",
            comparison_rule = Grader.Rule,
            cases = eval.Cases.Select(calc.Compute).ToList(),
        };
        File.WriteAllText(Repo.ExpectedFile, JsonSerializer.Serialize(file, ExpectedFile.Json));
        Console.WriteLine($"expected results for {file.cases.Count} cases written to eval/expected.json (fixture {f.FixtureId})");
        return 0;
    }

    // ---------- replay ----------
    public static async Task<int> Replay(string recordingDir, string runId, string outDir, string? readerConnection)
    {
        var f = Repo.Fixture();
        var eval = EvalFile.Load(Repo.CasesFile);
        var clock = ReferenceClock.Parse(eval.ReferenceClock, eval.Timezone);
        var translator = new RecordedTranslator(recordingDir);
        string executedOn;
        IEntityDirectory directory;
        IQueryExecutor? executor = null;
        if (readerConnection is not null)
        {
            directory = new SqlEntityDirectory(readerConnection);
            executor = new ScopedQueryExecutor(readerConnection);
            executedOn = Repo.Platform() + ", " + await ServerVersion(readerConnection);
        }
        else
        {
            directory = new FixtureEntityDirectory(f);
            executedOn = Repo.Platform() + ", no database (verification mode)";
        }
        var ctx = new PipelineContext(runId, clock, new FixtureRef { Id = f.FixtureId, Sha256 = f.Sha256 }, Repo.Versions(), executedOn);
        var pipeline = new AnswerPipeline(translator, directory, executor, ctx);
        Directory.CreateDirectory(Path.Combine(outDir, "traces"));
        var traces = new List<AnswerTrace>();
        foreach (var c in eval.Cases)
        {
            var t = await pipeline.AnswerAsync(c.Id, c.Question, new ScopeIdentity(c.TenantId, $"demo-user-tenant-{c.TenantId}"), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(outDir, "traces", c.Id + ".json"), t.ToJson());
            traces.Add(t);
            Console.WriteLine($"{c.Id,-4} {t.Outcome,-18} {t.ExecutionStatus,-12} rows {t.Rows.Count}");
        }
        var summary = Grade(runId, translator.Manifest.RecordingId, readerConnection is null ? "verification (no database)" : "sql replay", executedOn, f, eval, traces);
        await File.WriteAllTextAsync(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, Json));
        PrintSummary(summary);
        return 0;
    }

    static async Task<string> ServerVersion(string conn)
    {
        await using var c = new SqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT CONCAT('SQL Server ', CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)), ' ', CAST(SERVERPROPERTY('Edition') AS nvarchar(80)))", c);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Grades one trace against its case and its independent expectation.</summary>
    public static Grade GradeOne(string caseId, AnswerTrace t, FixtureSet f)
    {
        var c = EvalFile.Load(Repo.CasesFile).Cases.Single(x => x.Id == caseId);
        var e = ExpectedFile.Load(Repo.ExpectedFile).Cases.Single(x => x.CaseId == caseId);
        return Grader.GradeCase(c.Set, e, t, f, c.Spec?.AsOf, c.Spec?.Basis, c.Spec?.Measure);
    }

    public static RunSummary Grade(string runId, string recordingId, string mode, string executedOn, FixtureSet f, EvalFile eval, IReadOnlyList<AnswerTrace> traces)
    {
        var expected = ExpectedFile.Load(Repo.ExpectedFile);
        if (expected.FixtureSha256 != f.Sha256) throw new InvalidDataException("eval/expected.json was computed on another fixture; run `nlq expected` first");
        var s = new RunSummary
        {
            RunId = runId, RecordingId = recordingId, Mode = mode, ExecutedOn = executedOn,
            ExecutedAt = traces.Count > 0 ? traces[0].ExecutedAt : "", FixtureId = f.FixtureId, FixtureSha256 = f.Sha256,
            ModelId = string.Join(", ", traces.Select(t => t.Model.ModelId).Where(m => m is not null).Distinct()),
        };
        foreach (var t in traces)
        {
            var c = eval.Cases.Single(x => x.Id == t.CaseId);
            var e = expected.Cases.Single(x => x.CaseId == t.CaseId);
            s.Grades.Add(Grader.GradeCase(c.Set, e, t, f, c.Spec?.AsOf, c.Spec?.Basis, c.Spec?.Measure));
            s.TraceSha256[t.CaseId] = t.ContentSha256();
        }
        s.ByCategory = s.Grades.GroupBy(g => g.Category).ToDictionary(g => g.Key, g => g.GroupBy(x => x.Verdict).ToDictionary(v => v.Key, v => v.Count()));
        return s;
    }

    static void PrintSummary(RunSummary s)
    {
        Console.WriteLine();
        Console.WriteLine($"run {s.RunId} ({s.Mode}) on {s.ExecutedOn}");
        foreach (var (cat, verdicts) in s.ByCategory)
            Console.WriteLine($"  {cat,-14} {verdicts.Values.Sum(),2} case(s): " + string.Join(", ", verdicts.Select(v => $"{v.Key} {v.Value}")));
        foreach (var g in s.Grades.Where(g => !g.Passed))
            Console.WriteLine($"  FAILED {g.CaseId} ({g.Verdict}): {g.Summary}");
    }

    // ---------- verify (no database) ----------
    public static async Task<int> Verify(string recordingDir, string runDir)
    {
        var problems = new List<string>();
        var f = Repo.Fixture();
        var eval = EvalFile.Load(Repo.CasesFile);
        Console.WriteLine($"fixture        {f.FixtureId}");

        var expected = ExpectedFile.Load(Repo.ExpectedFile);
        if (expected.FixtureSha256 != f.Sha256) problems.Add("eval/expected.json fixture hash differs from fixture/*.csv");
        var recomputed = eval.Cases.Select(new ReferenceCalculator(f).Compute).ToList();
        var a = JsonSerializer.Serialize(recomputed, ExpectedFile.Json);
        var b = JsonSerializer.Serialize(expected.Cases.Select(c => new ExpectedCase(c.CaseId, c.TenantId, c.Category, c.Outcome, c.KeyColumns,
            c.Rows.Select(r => new ExpectedRow(r.Key, r.Status, r.Values, r.Reason)).ToList())).ToList(), ExpectedFile.Json);
        if (a != b) problems.Add("independent expected results recomputed from the fixture differ from eval/expected.json");
        else Console.WriteLine($"expected       recomputed independently, identical to eval/expected.json ({recomputed.Count} cases)");

        var summary = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(Path.Combine(runDir, "summary.json")), Json)!;
        var clock = ReferenceClock.Parse(eval.ReferenceClock, eval.Timezone);
        var translator = new RecordedTranslator(recordingDir);
        var dbless = new AnswerPipeline(translator, new FixtureEntityDirectory(f), null,
            new PipelineContext("verify", clock, new FixtureRef { Id = f.FixtureId, Sha256 = f.Sha256 }, Repo.Versions(), "verification"));
        var traces = new List<AnswerTrace>();
        var sqlMatches = 0;
        foreach (var c in eval.Cases)
        {
            var stored = AnswerTrace.Load(Path.Combine(runDir, "traces", c.Id + ".json"));
            traces.Add(stored);
            if (stored.Fixture.Sha256 != f.Sha256) problems.Add($"{c.Id}: trace was produced on another fixture");
            if (stored.Model.RawResponse != translator.Load(c.Id).Attempts.SingleOrDefault(x => x.N == translator.Load(c.Id).AttemptUsed)?.ResultText)
                problems.Add($"{c.Id}: recorded reply differs from the reply in the trace");
            var fresh = await dbless.AnswerAsync(c.Id, c.Question, new ScopeIdentity(c.TenantId, "verify"), CancellationToken.None);
            var same = fresh.Sql == stored.Sql && JsonSerializer.Serialize(fresh.Parameters) == JsonSerializer.Serialize(stored.Parameters)
                       && fresh.Outcome == (stored.Outcome == "error" && stored.Sql is not null ? "answered" : stored.Outcome);
            if (same) sqlMatches++; else problems.Add($"{c.Id}: replaying the recorded reply gives {fresh.Outcome} / different SQL than the stored run ({stored.Outcome})");
        }
        Console.WriteLine($"replay         {sqlMatches}/{eval.Cases.Count} recorded replies parse, validate and compile to the exact SQL and parameters stored in {Path.GetFileName(runDir)}");

        var regraded = Grade(summary.RunId, summary.RecordingId, summary.Mode, summary.ExecutedOn, f, eval, traces);
        var sameGrades = JsonSerializer.Serialize(regraded.Grades, Json) == JsonSerializer.Serialize(summary.Grades, Json);
        if (!sameGrades) problems.Add("grading the stored rows again gives a different result than summary.json");
        Console.WriteLine($"grades         stored rows from {summary.RunId} ({summary.ExecutedOn}) re-graded against expected: {(sameGrades ? "same verdicts" : "DIFFERENT")}");
        PrintSummary(regraded);
        Console.WriteLine();
        Console.WriteLine("not executed in this mode: SQL Server, row-level security, write denial, timeouts. Run `make sql` for those.");
        foreach (var p in problems) Console.WriteLine("PROBLEM " + p);
        return problems.Count == 0 ? 0 : 1;
    }

    // ---------- compare two runs ----------
    public static int Compare(string runA, string runB)
    {
        var a = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(Path.Combine(runA, "summary.json")), Json)!;
        var b = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(Path.Combine(runB, "summary.json")), Json)!;
        var diff = a.TraceSha256.Where(kv => b.TraceSha256.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        Console.WriteLine(diff.Count == 0
            ? $"identical: all {a.TraceSha256.Count} answers (outcome, SQL, parameters, rows) match between {a.RunId} and {b.RunId}"
            : "different answers: " + string.Join(", ", diff));
        return diff.Count == 0 ? 0 : 1;
    }
}
