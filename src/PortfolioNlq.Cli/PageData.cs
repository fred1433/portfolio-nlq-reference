using System.Text.Json;
using PortfolioNlq.Audit;
using PortfolioNlq.Catalog;
using PortfolioNlq.Evaluation;
using PortfolioNlq.Export;
using PortfolioNlq.Reference;

namespace PortfolioNlq.Cli;

/// <summary>Builds the data file the presentation page reads, and one workbook per answered case, from stored runs only.</summary>
public static class PageData
{
    public static int Write(string runDir, string recordingDir, string outPath, string? beforeRunDir, string? beforeRecordingDir)
    {
        var eval = EvalFile.Load(Repo.CasesFile);
        var expected = ExpectedFile.Load(Repo.ExpectedFile);
        var f = Repo.Fixture();
        var summary = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(Path.Combine(runDir, "summary.json")), Commands.Json)!;
        var manifest = new RecordedTranslator(recordingDir).Manifest;
        var downloads = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "downloads");
        Directory.CreateDirectory(downloads);

        var cases = eval.Cases.Select(c =>
        {
            var t = AnswerTrace.Load(Path.Combine(runDir, "traces", c.Id + ".json"));
            string? xlsx = null;
            if (t.Outcome == "answered" && t.Rows.Count > 0)
            {
                xlsx = $"downloads/{c.Id}-{summary.RunId}.xlsx";
                ExcelExporter.Write(t, Path.Combine(downloads, Path.GetFileName(xlsx)));
            }
            return new
            {
                c.Id, c.Category, c.Family, c.Set, c.Question, c.Why, c.TenantId,
                TenantName = f.Tenants.Single(x => x.TenantId == c.TenantId).Name,
                Trace = t,
                Expected = expected.Cases.Single(e => e.CaseId == c.Id),
                Grade = summary.Grades.Single(g => g.CaseId == c.Id),
                Xlsx = xlsx,
            };
        }).ToList();

        object? before = null;
        if (beforeRunDir is not null && beforeRecordingDir is not null)
        {
            var bs = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(Path.Combine(beforeRunDir, "summary.json")), Commands.Json)!;
            before = new
            {
                Summary = bs,
                Recording = new RecordedTranslator(beforeRecordingDir).Manifest,
                Failed = bs.Grades.Where(g => !g.Passed).Select(g => new { Grade = g, Trace = AnswerTrace.Load(Path.Combine(beforeRunDir, "traces", g.CaseId + ".json")) }).ToList(),
            };
        }

        var data = new
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Run = summary,
            Recording = manifest,
            Prompt = new { Version = PromptBuilder.Version, Sha256 = PromptBuilder.Sha256(), Text = PromptBuilder.SystemPrompt() },
            Fixture = new { Id = f.FixtureId, f.Sha256 },
            Definitions = QueryCatalog.Definitions,
            eval.ReferenceClock, eval.Timezone,
            Cases = cases,
            Before = before,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(data, AnswerTrace.Json));
        Console.WriteLine($"page data: {cases.Count} cases, {cases.Count(c => c.Xlsx is not null)} workbooks -> {outPath}");
        return 0;
    }
}
