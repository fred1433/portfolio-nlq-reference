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
    public List<string> DetailKeyColumns { get; set; } = [];
    public List<ExpectedRowDto> DetailRows { get; set; } = [];
}

/// <summary>What the question means, written per case in eval/cases.json, read here independently of the reference project.</summary>
public sealed class SemanticSpec
{
    public string? Measure { get; set; }
    public string? DimensionType { get; set; }
    public string? DimensionValue { get; set; }
    public string? AsOf { get; set; }
    public string? AsOfTime { get; set; }
    public string Basis { get; set; } = "trade";
    public string? ThresholdDirection { get; set; }
    public decimal? ThresholdPoints { get; set; }
    public List<string> GroupBy { get; set; } = [];
    public string? Side { get; set; }
    public string? AssetClass { get; set; }
    public string Status { get; set; } = "open";
    public List<string> AccountNumbers { get; set; } = [];
    public List<string> AccountNames { get; set; } = [];
    public List<string> HouseholdNames { get; set; } = [];
    public List<string> CustodianNames { get; set; } = [];
    public List<string> ModelNames { get; set; } = [];
    public List<string> Symbols { get; set; } = [];
}

public sealed class CaseExpectation
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Set { get; set; } = "";
    public int TenantId { get; set; }
    public List<string>? ExpectedOutcomes { get; set; }
    public SemanticSpec? Spec { get; set; }
}

public sealed class CaseFile
{
    public string ReferenceClock { get; set; } = "";
    public string Timezone { get; set; } = "";
    public List<CaseExpectation> Cases { get; set; } = [];
    public static CaseFile Load(string path) => JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(path), ExpectedFile.Json)!;
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
/// Grades a trace in two steps: first what the question was read as (tenant, measure, sleeve, threshold and direction,
/// grouping, filters, date, basis or cutoff, outcome), then the result rows and the contributing lines.
/// Numbers are compared after rounding both sides to 10 decimal places (half away from zero).
/// For authorization cases, "no unauthorized data" and "request handled correctly" are separate checks.
/// </summary>
public static class Grader
{
    public const int Decimals = 10;
    public const string Rule = "numbers compared after rounding both sides to 10 decimal places, half away from zero; display rounds to 2";

    public static Grade GradeCase(CaseExpectation c, ExpectedCaseDto e, AnswerTrace t, FixtureSet f)
    {
        var checks = new List<Check> { new("tenant", t.TenantId == c.TenantId, $"asked as tenant {t.TenantId}, expected {c.TenantId}") };
        if (t.Outcome is "error" or "cancelled")
            checks.Add(new("handled", false, $"{t.Outcome} at stage {t.OutcomeSource}: {t.OutcomeMessage ?? t.ExecutionNote}"));
        switch (c.Category)
        {
            case "answer":
            {
                if (t.Outcome != "answered")
                {
                    var verdict = t.Outcome is "clarify" or "refuse" or "no_match_in_scope" ? "unjustified_refusal" : "error";
                    checks.Add(new("outcome", false, $"expected an answer, got {t.Outcome}: {t.OutcomeMessage}"));
                    return new(c.Id, c.Category, c.Set, verdict, false, checks, $"Did not answer ({t.Outcome}).");
                }
                CompareSemantics(c.Spec!, t, checks);
                CompareRows(e, t, checks);
                var ok = checks.All(x => x.Passed);
                return new(c.Id, c.Category, c.Set, ok ? "correct" : "wrong", ok, checks,
                    ok ? "Reading, rows, values and contributing lines match." : string.Join(" ", checks.Where(x => !x.Passed).Select(x => x.Detail)));
            }
            case "clarification":
            {
                var verdict = t.Outcome switch { "clarify" => "clarified", "answered" => "guessed", "refuse" => "refused_instead", _ => "error" };
                checks.Add(new("outcome", verdict == "clarified", $"{t.Outcome}: {t.OutcomeMessage}"));
                var ok = checks.All(x => x.Passed);
                return new(c.Id, c.Category, c.Set, verdict, ok, checks, t.OutcomeMessage ?? t.Outcome);
            }
            case "refusal":
            {
                var verdict = t.Outcome switch { "refuse" => "refused", "answered" => "answered_out_of_scope", "clarify" => "clarified_instead", _ => "error" };
                checks.Add(new("outcome", verdict == "refused", $"{t.Outcome}: {t.OutcomeMessage}"));
                var ok = checks.All(x => x.Passed);
                return new(c.Id, c.Category, c.Set, verdict, ok, checks, t.OutcomeMessage ?? t.Outcome);
            }
            case "authorization":
            {
                var foreign = ForeignRows(t, f);
                var noLeak = foreign.Count == 0;
                checks.Add(new("no unauthorized data", noLeak, noLeak ? "no row of another tenant" : "leaked: " + string.Join(", ", foreign)));
                var allowed = c.ExpectedOutcomes ?? (e.Outcome == "rows" ? ["answered", "no_match_in_scope"] : ["refuse"]);
                var handled = new List<Check> { new("outcome", allowed.Contains(t.Outcome), $"{t.Outcome}, expected {string.Join(" or ", allowed)}") };
                if (t.Outcome is "answered" or "no_match_in_scope" && e.Outcome == "rows" && allowed.Contains(t.Outcome))
                {
                    if (t.Outcome == "answered") CompareSemantics(c.Spec!, t, handled);
                    CompareRows(e, t, handled);
                }
                var handledOk = handled.All(x => x.Passed) && checks.All(x => x.Name != "handled");
                checks.Add(new("request handled correctly", handledOk, string.Join("; ", handled.Where(x => !x.Passed).Select(x => x.Detail).DefaultIfEmpty("yes"))));
                var verdict = !noLeak ? "leak" : t.Outcome is "error" or "cancelled" ? "error" : handledOk ? "held" : "misread_held";
                var ok = noLeak && handledOk && checks.All(x => x.Passed);
                return new(c.Id, c.Category, c.Set, verdict, ok, checks, $"{t.Outcome}: {t.OutcomeMessage ?? t.ExecutionNote}");
            }
        }
        throw new InvalidDataException("category " + c.Category);
    }

    static readonly Dictionary<string, string> GroupToColumn = new()
    {
        ["account"] = "account_number", ["security"] = "symbol", ["custodian"] = "custodian", ["block"] = "block_id", ["side"] = "side", ["household"] = "household",
    };

    /// <summary>The reading of the question, compared field by field with the case's own statement of its meaning.</summary>
    static void CompareSemantics(SemanticSpec s, AnswerTrace t, List<Check> checks)
    {
        var q = t.Interpretation!;
        void Eq(string name, object? got, object? want) =>
            checks.Add(new(name, Equals(got, want), $"{name} {got ?? "none"}, expected {want ?? "none"}"));
        Eq("measure", q.Measure, s.Measure);
        if (s.Measure == "drift")
        {
            Eq("sleeve", $"{q.DimensionType}:{q.DimensionValue}", $"{s.DimensionType}:{s.DimensionValue}");
            Eq("threshold", $"{q.ThresholdDirection} {q.ThresholdPoints?.ToString(CultureInfo.InvariantCulture)}", $"{s.ThresholdDirection} {s.ThresholdPoints?.ToString(CultureInfo.InvariantCulture)}");
            Eq("grouping", string.Join(",", q.GroupBy), s.GroupBy.Count == 0 ? "account" : string.Join(",", s.GroupBy));
            Eq("as-of date", q.AsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.AsOf);
            Eq("date basis", q.DateBasis, s.Basis);
        }
        else
        {
            Eq("side", q.Side, s.Side);
            Eq("asset class", q.AssetClass, s.AssetClass);
            Eq("status", q.Status, s.Status);
            Eq("cutoff", q.AsOfTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), s.AsOfTime);
            var got = q.GroupBy.Select(g => GroupToColumn[g]).Where(g => g is not "symbol" and not "side").Order().ToList();
            var want = s.GroupBy.Where(g => g is not "symbol" and not "side").Order().ToList();
            Eq("grouping", string.Join(",", got), string.Join(",", want));
        }
        // Filters: each stated name or code must be resolved, and nothing else.
        string Name(string matched) => matched.Contains(" (") ? matched[..matched.LastIndexOf(" (", StringComparison.Ordinal)] : matched;
        string? Code(string matched) => matched.EndsWith(')') ? matched[(matched.LastIndexOf(" (", StringComparison.Ordinal) + 2)..^1] : null;
        var resolved = q.Filters.SelectMany(f => f.Matched.Select(m => (f.Field, Name: Name(m), Code: Code(m)))).ToList();
        var wanted = s.AccountNames.Select(n => ("account", n, true)).Concat(s.AccountNumbers.Select(n => ("account", n, false)))
            .Concat(s.HouseholdNames.Select(n => ("household", n, true))).Concat(s.CustodianNames.Select(n => ("custodian", n, true)))
            .Concat(s.ModelNames.Select(n => ("model", n, true))).Concat(s.Symbols.Select(n => ("security", n, false))).ToList();
        var missing = wanted.Where(w => !resolved.Any(r => r.Field == w.Item1 && (w.Item3 ? r.Name == w.Item2 : r.Code == w.Item2))).ToList();
        var extra = resolved.Where(r => !wanted.Any(w => r.Field == w.Item1 && (w.Item3 ? r.Name == w.Item2 : r.Code == w.Item2))).ToList();
        checks.Add(new("filters", missing.Count == 0 && extra.Count == 0,
            missing.Count + extra.Count == 0 ? $"{resolved.Count} filter(s) as stated"
            : $"missing [{string.Join(", ", missing.Select(m => m.Item1 + " " + m.Item2))}] extra [{string.Join(", ", extra.Select(x => x.Field + " " + x.Name))}]"));
    }

    static void CompareRows(ExpectedCaseDto e, AnswerTrace t, List<Check> checks)
    {
        CompareSet("rows", e.KeyColumns, e.Rows, t.Rows, checks, true);
        checks.Add(new("completeness", t.ExecutionStatus == "complete" || (t.Outcome == "no_match_in_scope" && e.Rows.Count == 0), $"execution {t.ExecutionStatus}"));
        if (e.DetailKeyColumns.Count > 0) CompareSet("contributing lines", e.DetailKeyColumns, e.DetailRows, t.DetailRows, checks, false);
    }

    static void CompareSet(string what, List<string> keyColumns, List<ExpectedRowDto> expected, List<Dictionary<string, string?>> actualRows, List<Check> checks, bool statusColumn)
    {
        string KeyOf(Dictionary<string, string?> row) => string.Join(" | ", keyColumns.Select(k => row.GetValueOrDefault(k) ?? ""));
        var actual = actualRows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var expectedKeys = expected.Select(r => r.Key).ToHashSet();
        var missing = expectedKeys.Where(k => !actual.ContainsKey(k)).ToList();
        var extra = actual.Keys.Where(k => !expectedKeys.Contains(k)).ToList();
        var dupes = actual.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        checks.Add(new($"{what}: identity", missing.Count == 0 && extra.Count == 0 && dupes.Count == 0,
            missing.Count + extra.Count + dupes.Count == 0 ? $"{expected.Count} {what} match by {string.Join(", ", keyColumns)}"
            : $"{what}: missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}] duplicated [{string.Join(", ", dupes)}]"));

        var problems = new List<string>();
        foreach (var er in expected.Where(r => actual.ContainsKey(r.Key)))
        {
            var ar = actual[er.Key][0];
            if (statusColumn)
            {
                var status = ar.GetValueOrDefault("row_status") ?? "Complete";
                if (status != er.Status) { problems.Add($"{er.Key}: status {status}, expected {er.Status}"); continue; }
                if (er.Status == "Incomplete")
                {
                    var got = (ar.GetValueOrDefault("missing_detail") ?? "").Split("; ").ToHashSet();
                    var want = (er.Reason ?? "").Split("; ").ToHashSet();
                    if (!got.SetEquals(want)) problems.Add($"{er.Key}: reason '{ar.GetValueOrDefault("missing_detail")}', expected '{er.Reason}'");
                }
                if (er.Status != "Complete" && ar.GetValueOrDefault("weight_pct") is not null) problems.Add($"{er.Key}: a row without valuation carries a weight");
            }
            foreach (var (col, want) in er.Values)
            {
                var got = ar.GetValueOrDefault(col);
                if (!SameValue(got, want)) problems.Add($"{er.Key}.{col}: {got ?? "null"}, expected {want ?? "null"}");
            }
        }
        checks.Add(new($"{what}: values", problems.Count == 0, problems.Count == 0 ? Rule : string.Join("; ", problems)));
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
