using System.Diagnostics;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Audit;

public sealed record PipelineContext(string RunId, ReferenceClock Clock, FixtureRef Fixture, Versions Versions, string ExecutedOn);

/// <summary>
/// question -> model (typed query only) -> parse -> validate and resolve under scope -> compile -> execute under scope -> trace.
/// Every step writes into the trace, including the ones that stop the answer.
/// </summary>
public sealed class AnswerPipeline(ITranslator translator, IEntityDirectory directory, IQueryExecutor? executor, PipelineContext ctx)
{
    public async Task<AnswerTrace> AnswerAsync(string caseId, string question, ScopeIdentity scope, CancellationToken ct)
    {
        var t = new AnswerTrace
        {
            RunId = ctx.RunId, CaseId = caseId, Question = question, TenantId = scope.TenantId, UserId = scope.UserId,
            Scope = $"tenant {scope.TenantId}, set by the host from the authenticated user, pinned read-only in SESSION_CONTEXT",
            ReferenceClock = ctx.Clock.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), TimeZone = ctx.Clock.TimeZone,
            Versions = ctx.Versions, Fixture = ctx.Fixture, ExecutedOn = ctx.ExecutedOn,
            ExecutedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        var sw = Stopwatch.StartNew();

        var translation = await translator.TranslateAsync(caseId, question, ct);
        t.Model = translation.Call;
        if (translation.Call.ModelDurationMs is long md) t.DurationsMs["model"] = md;

        sw.Restart();
        var (output, parseError) = ModelOutputParser.Parse(translation.RawResponse);
        t.DurationsMs["parse"] = sw.ElapsedMilliseconds;
        if (output is null)
        {
            t.Outcome = "rejected"; t.ParseError = parseError; t.OutcomeSource = "parser";
            t.OutcomeMessage = "The model reply did not match the query shape, so nothing was run. " + parseError;
            return t;
        }
        t.TypedQuery = output;

        sw.Restart();
        DirectorySnapshot dir;
        try { dir = await directory.LoadAsync(scope, ct); }
        catch (ScopeException e)
        {
            t.Outcome = "error"; t.OutcomeSource = "scope"; t.OutcomeMessage = e.Message;
            return t;
        }
        t.DirectorySource = dir.Source;
        var v = Validator.Validate(output, dir, ctx.Clock);
        t.DurationsMs["validate"] = sw.ElapsedMilliseconds;
        t.Interpretation = v.Query;
        t.OutcomeMessage = v.Message;
        t.OutcomeSource = v.Source;
        t.Outcome = v.Kind switch
        {
            OutcomeKind.Query => "answered", OutcomeKind.Clarify => "clarify", OutcomeKind.Refuse => "refuse",
            OutcomeKind.Rejected => "rejected", OutcomeKind.NoMatchInScope => "no_match_in_scope", _ => "error",
        };
        if (v.Query is null) return t;

        sw.Restart();
        var compiled = SqlCompiler.Compile(v.Query);
        t.DurationsMs["compile"] = sw.ElapsedMilliseconds;
        t.Sql = compiled.Sql; t.DetailSql = compiled.DetailSql; t.Parameters = compiled.Parameters.ToList(); t.KeyColumns = compiled.KeyColumns.ToList();

        if (v.Kind == OutcomeKind.NoMatchInScope) { t.ExecutionStatus = "not executed: no matching name in scope"; return t; }
        if (executor is null) { t.ExecutionStatus = "not executed: no database in this mode"; return t; }

        var result = await executor.ExecuteAsync(scope, compiled, ct);
        t.DurationsMs["execute"] = result.DurationMs;
        t.ExecutionStatus = result.Status;
        t.ExecutionNote = result.Describe();
        t.Columns = result.Columns.ToList();
        t.Rows = result.Rows.ToList();
        t.DetailRows = result.DetailRows.ToList();
        if (result.Status != "complete" && result.Status != "truncated") t.Outcome = "error";
        return t;
    }
}
