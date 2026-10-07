using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioNlq.Audit;
using PortfolioNlq.Fixture;

namespace PortfolioNlq.Evaluation;

public sealed class ExpectedRowDto
{
    public string Key { get; set; } = "";
    public string Status { get; set; } = "";
    public Dictionary<string, string?> Values { get; set; } = [];
    public string? Reason { get; set; }
}

public sealed class ExpectedCaseDto
{
    public string CaseId { get; set; } = "";
    public int TenantId { get; set; }
    public string Category { get; set; } = "";
    public string Outcome { get; set; } = "";
    public List<string> KeyColumns { get; set; } = [];
    public List<ExpectedRowDto> Rows { get; set; } = [];
}

public sealed class ExpectedFile
{
    public string FixtureSha256 { get; set; } = "";
    public string CasesSha256 { get; set; } = "";
    public string ComputedAt { get; set; } = "";
    public string Generator { get; set; } = "";
    public string ComparisonRule { get; set; } = "";
    public List<ExpectedCaseDto> Cases { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ExpectedFile Load(string path) => JsonSerializer.Deserialize<ExpectedFile>(File.ReadAllText(path), Json)!;
}

public sealed record Check(string Name, bool Passed, string Detail);

public sealed record Grade(string CaseId, string Category, string Set, string Verdict, bool Passed, IReadOnlyList<Check> Checks, string Summary);

/// <summary>
/// Compares a trace with the independent expectation. Numbers are compared after rounding both sides to
/// 10 decimal places (half away from zero); unrounded values stay in the trace and in expected.json.
/// </summary>
public static class Grader
{
    public const int Decimals = 10;
    public const string Rule = "numbers compared after rounding both sides to 10 decimal places, half away from zero; display rounds to 2";

    public static Grade GradeCase(string set, ExpectedCaseDto e, AnswerTrace t, FixtureSet f, string? expectedAsOf, string? expectedBasis, string? expectedMeasure)
    {
        var checks = new List<Check>();
        switch (e.Category)
        {
            case "answer":
            {
                if (t.Outcome != "answered")
                {
                    var verdict = t.Outcome is "clarify" or "refuse" or "no_match_in_scope" ? "unjustified_refusal" : "error";
                    return new(e.CaseId, e.Category, set, verdict, false, [new("outcome", false, $"expected an answer, got {t.Outcome}: {t.OutcomeMessage}")], $"Did not answer ({t.Outcome}).");
                }
                CompareInterpretation(t, expectedAsOf, expectedBasis, expectedMeasure, checks);
                CompareRows(e, t, checks);
                var ok = checks.All(c => c.Passed);
                return new(e.CaseId, e.Category, set, ok ? "correct" : "wrong", ok, checks, ok ? "Rows, values and dates match." : string.Join(" ", checks.Where(c => !c.Passed).Select(c => c.Detail)));
            }
            case "clarification":
            {
                var verdict = t.Outcome switch { "clarify" => "clarified", "answered" => "guessed", "refuse" => "refused_instead", _ => "error" };
                return new(e.CaseId, e.Category, set, verdict, verdict == "clarified", [new("outcome", verdict == "clarified", $"{t.Outcome}: {t.OutcomeMessage}")], t.OutcomeMessage ?? t.Outcome);
            }
            case "refusal":
            {
                var verdict = t.Outcome switch { "refuse" => "refused", "answered" => "answered_out_of_scope", "clarify" => "clarified_instead", _ => "error" };
                return new(e.CaseId, e.Category, set, verdict, verdict == "refused", [new("outcome", verdict == "refused", $"{t.Outcome}: {t.OutcomeMessage}")], t.OutcomeMessage ?? t.Outcome);
            }
            case "authorization":
            {
                var foreign = ForeignRows(t, f);
                checks.Add(new("no rows from another tenant", foreign.Count == 0, foreign.Count == 0 ? "none" : "leaked: " + string.Join(", ", foreign)));
                if (e.Outcome == "rows")
                {
                    if (t.Outcome is "answered" or "no_match_in_scope") CompareRows(e, t, checks);
                    else if (e.Rows.Count > 0) checks.Add(new("rows", false, $"expected {e.Rows.Count} own row(s), got {t.Outcome}"));
                }
                else if (t.Outcome == "answered" && t.Rows.Count > 0)
                    checks.Add(new("nothing answered", false, $"returned {t.Rows.Count} own row(s) for a request that should have returned nothing"));
                var ok = checks.All(c => c.Passed);
                var verdict = foreign.Count > 0 ? "leak" : ok ? "held" : "misread";
                return new(e.CaseId, e.Category, set, verdict, ok, checks, $"{t.Outcome}: {t.OutcomeMessage ?? t.ExecutionNote}");
            }
        }
        throw new InvalidDataException("category " + e.Category);
    }

    static void CompareInterpretation(AnswerTrace t, string? asOf, string? basis, string? measure, List<Check> checks)
    {
        var q = t.Interpretation!;
        if (measure is not null) checks.Add(new("measure", q.Measure == measure, $"measure {q.Measure}, expected {measure}"));
        if (asOf is not null) checks.Add(new("as-of date", q.AsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == asOf, $"as of {q.AsOf:yyyy-MM-dd}, expected {asOf}"));
        if (basis is not null && measure == "drift") checks.Add(new("date basis", q.DateBasis == basis, $"basis {q.DateBasis}, expected {basis}"));
    }

    static void CompareRows(ExpectedCaseDto e, AnswerTrace t, List<Check> checks)
    {
        string KeyOf(Dictionary<string, string?> row) => string.Join(" | ", e.KeyColumns.Select(k => row.GetValueOrDefault(k) ?? ""));
        var actual = t.Rows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var expectedKeys = e.Rows.Select(r => r.Key).ToHashSet();
        var missing = expectedKeys.Where(k => !actual.ContainsKey(k)).ToList();
        var extra = actual.Keys.Where(k => !expectedKeys.Contains(k)).ToList();
        var dupes = actual.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        checks.Add(new("row identity", missing.Count == 0 && extra.Count == 0 && dupes.Count == 0,
            missing.Count + extra.Count + dupes.Count == 0 ? $"{e.Rows.Count} row(s) match by {string.Join(", ", e.KeyColumns)}"
            : $"missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}] duplicated [{string.Join(", ", dupes)}]"));
        checks.Add(new("completeness", t.ExecutionStatus == "complete" || (t.Outcome == "no_match_in_scope" && e.Rows.Count == 0), $"execution {t.ExecutionStatus}"));

        var valueProblems = new List<string>();
        foreach (var er in e.Rows.Where(r => actual.ContainsKey(r.Key)))
        {
            var ar = actual[er.Key][0];
            var status = ar.GetValueOrDefault("row_status") ?? "Complete";
            if (status != er.Status) { valueProblems.Add($"{er.Key}: status {status}, expected {er.Status}"); continue; }
            if (er.Status == "Incomplete")
            {
                var got = (ar.GetValueOrDefault("missing_detail") ?? "").Split("; ").ToHashSet();
                var want = (er.Reason ?? "").Split("; ").ToHashSet();
                if (!got.SetEquals(want)) valueProblems.Add($"{er.Key}: reason '{ar.GetValueOrDefault("missing_detail")}', expected '{er.Reason}'");
                if (ar.GetValueOrDefault("weight_pct") is not null) valueProblems.Add($"{er.Key}: incomplete row carries a weight");
                continue;
            }
            foreach (var (col, want) in er.Values)
            {
                var got = ar.GetValueOrDefault(col);
                if (!SameValue(got, want)) valueProblems.Add($"{er.Key}.{col}: {got ?? "null"}, expected {want ?? "null"}");
            }
        }
        checks.Add(new("values", valueProblems.Count == 0, valueProblems.Count == 0 ? Rule : string.Join("; ", valueProblems)));
    }

    public static bool SameValue(string? got, string? want)
    {
        if (got is null || want is null) return got == want;
        if (decimal.TryParse(got, NumberStyles.Number, CultureInfo.InvariantCulture, out var g) &&
            decimal.TryParse(want, NumberStyles.Number, CultureInfo.InvariantCulture, out var w))
            return Math.Round(g, Decimals, MidpointRounding.AwayFromZero) == Math.Round(w, Decimals, MidpointRounding.AwayFromZero);
        return got == want;
    }

    /// <summary>Any account number or household name in the rows that belongs to another tenant.</summary>
    public static List<string> ForeignRows(AnswerTrace t, FixtureSet f)
    {
        var foreignAccounts = f.Accounts.Where(a => a.TenantId != t.TenantId).Select(a => a.AccountNumber).ToHashSet();
        var foreignHouseholds = f.Households.Where(h => h.TenantId != t.TenantId).Select(h => h.Name).ToHashSet();
        return t.Rows.Concat(t.DetailRows)
            .SelectMany(r => new[] { r.GetValueOrDefault("account_number"), r.GetValueOrDefault("household") })
            .Where(v => v is not null && (foreignAccounts.Contains(v) || foreignHouseholds.Contains(v)))
            .Select(v => v!).Distinct().ToList();
    }
}
