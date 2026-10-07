using System.Diagnostics;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Audit;

public sealed record PipelineContext(string RunId, ReferenceClock Clock, FixtureRef Fixture, Versions Versions, string ExecutedOn);

/// <summary>
/// question -> model (typed query only) -> parse -> validate and resolve under scope -> compile -> execute under scope -> trace.
/// Every request returns a trace, including the ones that fail: the stage, the error category and the duration are recorded.
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
        var stage = "model";
        var sw = Stopwatch.StartNew();
        try
        {
            var translation = await translator.TranslateAsync(caseId, question, ct);
            t.Model = translation.Call;
            t.DurationsMs["model"] = translation.Call.ModelDurationMs ?? sw.ElapsedMilliseconds;

            stage = "parse"; sw.Restart();
            var (output, parseError) = ModelOutputParser.Parse(translation.RawResponse);
            t.DurationsMs["parse"] = sw.ElapsedMilliseconds;
            if (output is null)
            {
                t.Outcome = "rejected"; t.ParseError = parseError; t.OutcomeSource = "parser"; t.ErrorCategory = "validation";
                t.OutcomeMessage = "The model reply did not match the query shape, so nothing was run. " + parseError;
                return t;
            }
            t.TypedQuery = output;

            stage = "lookup"; sw.Restart();
            var dir = await directory.LoadAsync(scope, ct);
            t.DirectorySource = dir.Source;
            t.DurationsMs["lookup"] = sw.ElapsedMilliseconds;

            stage = "validate"; sw.Restart();
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
            if (v.Kind == OutcomeKind.Rejected) t.ErrorCategory = "validation";
            if (v.Query is null) return t;

            stage = "compile"; sw.Restart();
            var compiled = SqlCompiler.Compile(v.Query);
            t.DurationsMs["compile"] = sw.ElapsedMilliseconds;
            t.Sql = compiled.Sql; t.DetailSql = compiled.DetailSql; t.Parameters = compiled.Parameters.ToList(); t.KeyColumns = compiled.KeyColumns.ToList();

            if (v.Kind == OutcomeKind.NoMatchInScope) { t.ExecutionStatus = "not executed: no matching name in scope"; return t; }
            if (executor is null) { t.ExecutionStatus = "not executed: no database in this mode"; return t; }

            stage = "execute";
            var result = await executor.ExecuteAsync(scope, compiled, ct);
            t.DurationsMs["execute"] = result.DurationMs;
            t.ExecutionStatus = result.Status;
            t.ExecutionNote = result.Describe();
            t.Columns = result.Columns.ToList();
            t.Rows = result.Rows.ToList();
            t.DetailRows = result.DetailRows.ToList();
            if (result.Status is "cancelled") { t.Outcome = "cancelled"; t.OutcomeSource = stage; t.ErrorCategory = "cancelled"; }
            else if (result.Status is not ("complete" or "truncated")) { t.Outcome = "error"; t.OutcomeSource = stage; t.ErrorCategory = "execution"; }
            return t;
        }
        catch (OperationCanceledException e)
        {
            t.Outcome = "cancelled"; t.OutcomeSource = stage; t.ErrorCategory = "cancelled"; t.OutcomeMessage = e.Message;
            t.DurationsMs[stage] = sw.ElapsedMilliseconds;
            return t;
        }
        catch (Exception e)
        {
            t.Outcome = "error"; t.OutcomeSource = stage; t.OutcomeMessage = $"{e.GetType().Name}: {e.Message}";
            t.ErrorCategory = stage switch { "model" => "provider", "lookup" => "lookup", "compile" => "compile", "execute" => "execution", _ => "validation" };
            t.DurationsMs[stage] = sw.ElapsedMilliseconds;
            return t;
        }
    }
}
