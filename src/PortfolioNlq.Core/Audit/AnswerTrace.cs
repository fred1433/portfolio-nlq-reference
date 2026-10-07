using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Audit;

public sealed class ModelCall
{
    public string Provider { get; set; } = "";             // claude-cli (recorded) | azure-openai | ...
    public string Mode { get; set; } = "";                 // recorded | fresh
    public string? ModelId { get; set; }
    public string? RecordingId { get; set; }
    public int AttemptsMade { get; set; }
    public int AttemptUsed { get; set; }
    public string? RecordedAt { get; set; }
    public long? ModelDurationMs { get; set; }
    public List<string> Flags { get; set; } = [];
    public string PromptVersion { get; set; } = "";
    public string PromptSha256 { get; set; } = "";
    public string UserMessage { get; set; } = "";
    public string RawResponse { get; set; } = "";
}

public sealed class Versions
{
    public string Catalog { get; set; } = "";
    public string Compiler { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string App { get; set; } = "";
}

public sealed class FixtureRef
{
    public string Id { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// One answer, complete enough to debug by reading it: what was asked, how it was read, what ran, on which data, what came back.
/// </summary>
public sealed class AnswerTrace
{
    public string RunId { get; set; } = "";
    public string CaseId { get; set; } = "";
    public string Question { get; set; } = "";
    public string Outcome { get; set; } = "";              // answered | clarify | refuse | rejected | no_match_in_scope | error
    public string? OutcomeMessage { get; set; }
    public string? OutcomeSource { get; set; }
    public int TenantId { get; set; }
    public string UserId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string ReferenceClock { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public ModelCall Model { get; set; } = new();
    public ModelOutput? TypedQuery { get; set; }
    public string? ParseError { get; set; }
    public ResolvedQuery? Interpretation { get; set; }
    public string? DirectorySource { get; set; }
    public Versions Versions { get; set; } = new();
    public FixtureRef Fixture { get; set; } = new();
    public string? Sql { get; set; }
    public string? DetailSql { get; set; }
    public List<QueryParameter> Parameters { get; set; } = [];
    public List<string> KeyColumns { get; set; } = [];
    public string ExecutionStatus { get; set; } = "not executed";
    public string? ExecutionNote { get; set; }
    public List<string> Columns { get; set; } = [];
    public List<Dictionary<string, string?>> Rows { get; set; } = [];
    public List<Dictionary<string, string?>> DetailRows { get; set; } = [];
    public Dictionary<string, long> DurationsMs { get; set; } = [];
    public string ExecutedAt { get; set; } = "";
    public string ExecutedOn { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static AnswerTrace FromJson(string json) => JsonSerializer.Deserialize<AnswerTrace>(json, Json)!;
    public static AnswerTrace Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Hash of what the answer depends on, leaving out timings: two runs that agree on it gave the same answer.</summary>
    public string ContentSha256()
    {
        var stable = new
        {
            CaseId, Question, Outcome, OutcomeMessage, TenantId, ReferenceClock, Model.RawResponse, Model.PromptSha256, Interpretation,
            Fixture.Sha256, Versions.Catalog, Versions.Compiler, Versions.Schema, Sql, DetailSql, Parameters, ExecutionStatus, Columns, Rows, DetailRows,
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stable, Json)))).ToLowerInvariant();
    }
}
