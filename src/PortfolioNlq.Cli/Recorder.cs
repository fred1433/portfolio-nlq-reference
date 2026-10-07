using System.Diagnostics;
using System.Text.Json;
using PortfolioNlq.Audit;
using PortfolioNlq.Catalog;
using PortfolioNlq.Reference;

namespace PortfolioNlq.Cli;

/// <summary>
/// Records model replies through the Claude Code CLI (`claude -p`), on a subscription, so the demo costs nothing per call.
/// Fixed system prompt; tools, MCP servers, settings files and slash commands disabled; run from an empty directory.
/// Every attempt is kept, failures included. A retry happens only when the CLI itself fails (non-zero exit or timeout),
/// never because the reply looks wrong.
/// </summary>
public static class Recorder
{
    public const string RetryRule = "retry only when the CLI exits non-zero or times out (max 3 attempts); a reply that does not parse is kept and graded as is";

    public static string[] Flags(string model, string systemPrompt) =>
    [
        "-p", "--output-format", "json", "--model", model, "--system-prompt", systemPrompt,
        "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
        "--setting-sources", "", "--disable-slash-commands", "--no-session-persistence",
    ];

    public static async Task RunAsync(string outDir, string model, IReadOnlyCollection<string>? only, string? note)
    {
        var cases = EvalFile.Load(Repo.CasesFile).Cases.Where(c => only is null || only.Contains(c.Id)).ToList();
        Directory.CreateDirectory(Path.Combine(outDir, "cases"));
        var system = PromptBuilder.SystemPrompt();
        await File.WriteAllTextAsync(Path.Combine(outDir, "system_prompt.txt"), system);
        var empty = Directory.CreateTempSubdirectory("nlq-record-").FullName;

        var manifest = new RecordingManifest
        {
            RecordingId = Path.GetFileName(Path.TrimEndingDirectorySeparator(outDir)),
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Provider = "claude-cli",
            CliVersion = (await Run(["--version"], empty, TimeSpan.FromSeconds(30))).Stdout.Trim(),
            ModelRequested = model,
            Flags = Flags(model, "<system_prompt.txt>").ToList(),
            PromptVersion = PromptBuilder.Version,
            PromptSha256 = PromptBuilder.Sha256(),
            ExpectedResultsInContext = false,
            CasesSha256 = Repo.FileSha256(Repo.CasesFile),
            RetryRule = RetryRule,
            Note = note,
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, RecordingManifest.Json));

        foreach (var c in cases)
        {
            var rec = new RecordedCase { CaseId = c.Id, Question = c.Question, UserMessage = PromptBuilder.UserMessage(c.Question) };
            for (var n = 1; n <= 3; n++)
            {
                var started = DateTimeOffset.UtcNow;
                var r = await Run([.. Flags(model, system), rec.UserMessage], empty, TimeSpan.FromMinutes(3));
                var attempt = new RecordingAttempt
                {
                    N = n, StartedAt = started.ToString("yyyy-MM-ddTHH:mm:ssZ"), DurationMs = r.DurationMs, ExitCode = r.ExitCode,
                    Stdout = r.Stdout, Stderr = r.Stderr,
                };
                if (r.ExitCode == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(r.Stdout);
                        attempt.ResultText = doc.RootElement.GetProperty("result").GetString();
                        if (doc.RootElement.TryGetProperty("modelUsage", out var mu))
                            attempt.ModelId = string.Join(",", mu.EnumerateObject().Select(p => p.Name));
                    }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { attempt.Stderr += "\n[recorder] CLI output not readable: " + e.Message; }
                }
                rec.Attempts.Add(attempt);
                if (r.ExitCode == 0 && attempt.ResultText is not null) { rec.AttemptUsed = n; break; }
            }
            await File.WriteAllTextAsync(Path.Combine(outDir, "cases", c.Id + ".json"), JsonSerializer.Serialize(rec, RecordingManifest.Json));
            Console.WriteLine($"{c.Id}: {rec.Attempts.Count} attempt(s), used {rec.AttemptUsed?.ToString() ?? "none"}: {Short(rec.Attempts[^1].ResultText)}");
        }
    }

    static string Short(string? s) => s is null ? "(no reply)" : s.Length > 110 ? s[..110].ReplaceLineEndings(" ") + "..." : s.ReplaceLineEndings(" ");

    sealed record ProcResult(int ExitCode, string Stdout, string Stderr, long DurationMs);

    static async Task<ProcResult> Run(IEnumerable<string> args, string cwd, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("NLQ_CLAUDE_BIN") ?? "claude")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); return new(-1, await so, await se + "\n[recorder] timeout", sw.ElapsedMilliseconds); }
        return new(p.ExitCode, await so, await se, sw.ElapsedMilliseconds);
    }
}
