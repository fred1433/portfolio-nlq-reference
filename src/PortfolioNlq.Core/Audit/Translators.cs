using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using PortfolioNlq.Catalog;

namespace PortfolioNlq.Audit;

public sealed record Translation(string RawResponse, ModelCall Call);

/// <summary>Question in, raw model text out. The rest of the pipeline does not know or care which provider answered.</summary>
public interface ITranslator
{
    Task<Translation> TranslateAsync(string caseId, string question, CancellationToken ct);
}

public sealed class RecordingAttempt
{
    public int N { get; set; }
    public string StartedAt { get; set; } = "";
    public long DurationMs { get; set; }
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public string? ModelId { get; set; }
    public string? ResultText { get; set; }
}

public sealed class RecordedCase
{
    public string CaseId { get; set; } = "";
    public string Question { get; set; } = "";
    public string UserMessage { get; set; } = "";
    public List<RecordingAttempt> Attempts { get; set; } = [];
    public int? AttemptUsed { get; set; }
}

public sealed class RecordingManifest
{
    public string RecordingId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? CliVersion { get; set; }
    public string ModelRequested { get; set; } = "";
    public List<string> Flags { get; set; } = [];
    public string PromptVersion { get; set; } = "";
    public string PromptSha256 { get; set; } = "";
    public string SystemPromptFile { get; set; } = "system_prompt.txt";
    public bool ExpectedResultsInContext { get; set; }
    public string CasesSha256 { get; set; } = "";
    public string RetryRule { get; set; } = "";
    public string? Note { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Replays model replies recorded earlier with `nlq record`. No network, no key, same text every time.</summary>
public sealed class RecordedTranslator(string recordingDirectory) : ITranslator
{
    public RecordingManifest Manifest { get; } = JsonSerializer.Deserialize<RecordingManifest>(
        File.ReadAllText(Path.Combine(recordingDirectory, "manifest.json")), RecordingManifest.Json)!;

    public RecordedCase Load(string caseId) => JsonSerializer.Deserialize<RecordedCase>(
        File.ReadAllText(Path.Combine(recordingDirectory, "cases", caseId + ".json")), RecordingManifest.Json)!;

    public Task<Translation> TranslateAsync(string caseId, string question, CancellationToken ct)
    {
        var rec = Load(caseId);
        if (rec.Question != question) throw new InvalidDataException($"{caseId}: recorded question differs from the case file");
        var used = rec.Attempts.SingleOrDefault(a => a.N == rec.AttemptUsed);
        return Task.FromResult(new Translation(used?.ResultText ?? "", new ModelCall
        {
            Provider = Manifest.Provider,
            Mode = "recorded",
            ModelId = used?.ModelId,
            RecordingId = Manifest.RecordingId,
            AttemptsMade = rec.Attempts.Count,
            AttemptUsed = rec.AttemptUsed ?? 0,
            RecordedAt = used?.StartedAt,
            ModelDurationMs = used?.DurationMs,
            Flags = Manifest.Flags,
            PromptVersion = Manifest.PromptVersion,
            PromptSha256 = Manifest.PromptSha256,
            UserMessage = rec.UserMessage,
            RawResponse = used?.ResultText ?? "",
        }));
    }
}

/// <summary>
/// Fresh call through Microsoft.Extensions.AI. Any IChatClient works (Azure OpenAI is wired in the CLI).
/// Selected explicitly only; never used by tests or CI.
/// </summary>
public sealed class ChatClientTranslator(IChatClient client, string provider, string modelName) : ITranslator
{
    public async Task<Translation> TranslateAsync(string caseId, string question, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, PromptBuilder.SystemPrompt()), new ChatMessage(ChatRole.User, PromptBuilder.UserMessage(question))],
            new ChatOptions { Temperature = 0, ModelId = modelName }, ct);
        var text = response.Text ?? "";
        return new Translation(text, new ModelCall
        {
            Provider = provider, Mode = "fresh", ModelId = response.ModelId ?? modelName, AttemptsMade = 1, AttemptUsed = 1,
            RecordedAt = started.ToString("O"), ModelDurationMs = sw.ElapsedMilliseconds, PromptVersion = PromptBuilder.Version,
            PromptSha256 = PromptBuilder.Sha256(), UserMessage = PromptBuilder.UserMessage(question), RawResponse = text,
        });
    }
}
