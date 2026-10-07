# Portfolio questions over SQL Server, from a reviewed catalog

**Reference implementation built for this demo, on synthetic data.** It is an independent reference modelled on publicly described rebalancing and trading workflows for advisors and funds (hierarchical models, drift by tag, block orders and their allocations, per-fund allocations). It is not connected to any vendor's product.

**Our production LLM work is document analysis (tender analysis for a paying client, not named). We have not yet shipped natural-language-to-SQL to production for a client.** The controls that run there today: every extracted requirement carries a verbatim quote, and the reader's browser searches each quote again in the source text when the page renders (a quote that cannot be found is shown as unverified); workspaces are separated by Postgres row-level security; analyses are precomputed, so the same click returns the same answer.

## Run it

| Command | Needs | What it proves |
|---|---|---|
| `make verify` | .NET 10 SDK. No Docker, no database, no key. | Fixture hash, expected results recomputed independently, every recorded model reply parsed, validated and compiled to the exact SQL and parameters of the published run, stored rows re-graded, 38 unit tests. Prints what it did **not** execute: SQL Server, row-level security, write denial. |
| `make sql` | .NET 10 SDK, Docker, **Linux x86-64**. ARM is not supported. | SQL Server 2022 (image pinned by digest), 13 tests under the restricted login, then the published recording replayed through the real .NET path and compared answer by answer with the published run. |
| `make record` | Claude Code logged in. | Re-records the model replies with `claude -p` on a subscription. Never run by tests or CI. |
| `nlq ask` | Azure OpenAI endpoint, deployment, key or Entra ID. | A fresh call through `Microsoft.Extensions.AI`. Explicit only. Status: **compiled, not run against the provider.** |

CI runs `make verify` and `make sql` on `ubuntu-24.04` (x86-64) at every push. Open `PortfolioNlq.slnx` in Visual Studio or Rider and debug `PortfolioNlq.Cli` with `replay --recording recordings/rec-2026-10-07-r2 --run-id debug --out runs/local-debug`.

## Published results

Run `run-2026-10-07-r2-ci`, recording `rec-2026-10-07-r2` (prompt-v2, `claude-sonnet-5-5`), fixture `synthetic-2026-10-06-9cf1bbc394e9`, SQL Server 2022 on Linux x86-64. 28 questions, graded against results computed beforehand by separate code. No single score:

| Category | Result |
|---|---|
| Answers | 15 of 15 correct, 0 wrong, 0 unjustified refusals |
| Clarifications needed | 4 of 4 asked, 0 guessed |
| Refusals | 4 of 4 refused |
| Authorization and adversarial | 5 of 5 held, 0 rows from another tenant |

Every case lists its checks (measure, as-of date, date basis, row identity, completeness, values) in `runs/run-2026-10-07-r2-ci/summary.json`. One recording per prompt version: sampling variance was not measured. 28 cases is a reference, not a benchmark.

**The loop that produced this.** Run 1 (`runs/run-2026-10-07-r1-local`, kept as recorded) failed 10 of 28: all 9 drift questions hit a SQL Server error in the compiled query (`Cannot perform an aggregate function on an expression containing an aggregate or a subquery`), and each answer said the query failed rather than showing an empty list; X02 was refused by the model instead of answered for the asking firm. Fixes: the dimension is now joined instead of tested with a subquery inside `SUM` (`compiler-2026.10.2`, guarded by a unit test), and prompt-v2 stops treating a mention of other firms as a reason to refuse. X02 is now a regression case; X05, a holdout whose run 1 output was read while writing the fix, is requalified as regression too. A named, injected defect (`>=` instead of `>` on the threshold) is also kept as a test: the evaluation flags Okafor Roth IRA, which sits at exactly +2.00.

## How a question becomes SQL

```
question -> model -> typed query (JSON) -> validator -> compiler -> parameterised SQL -> SQL Server (scoped login) -> trace
```

1. **Model** (`Catalog/PromptBuilder.cs`): returns one JSON object: a measure, a sleeve, filters by name, a threshold, a date as the user said it, or `clarify` / `refuse`. It never writes SQL, joins, formulas, dates or a tenant. Unknown fields are rejected, not repaired (`Interpretation/ModelOutput.cs`).
2. **Validator** (`Interpretation/Validator.cs`): checks the measure against the catalog, resolves names to ids **through the same tenant scope as the answer**, resolves relative dates once against a recorded reference clock, applies and lists the approved defaults (USD, America/Chicago, trade-date positions, latest close, open allocations). A name matching two accounts, a date without a recorded close, a sleeve not stated: each becomes a question, never a guess.
3. **Compiler** (`Sql/SqlCompiler.cs`, `db/002_reporting.sql`): hand-written, reviewed formulas and joins over the `rpt` views. Values travel only as parameters.
4. **Execution** (`Sql/QueryExecutor.cs`): timeout, cancellation and a row cap with explicit outcomes. A timeout is never shown as "no positions"; a truncated list is never presented as complete.

The catalog is composable: drift takes an asset class or a tag, account or household grouping, filters by account, household, custodian or model, any recorded close, either date basis, and a threshold above, below or either way. Open allocations take side, asset class, open or all, filters, and any grouping by account, security, custodian or block.

**Definitions.** Weight = market value of the sleeve / market value of the whole account, **cash included**, in USD at the as-of close. Target = the model's weight for the sleeve, multiplied down the model hierarchy. Drift = weight minus target, in points. A position with no close or no FX rate on the as-of date makes the account **incomplete**, listed with the reason, never valued at zero. Numbers are compared after rounding to 10 decimal places (half away from zero); traces keep every digit SQL Server returned; display rounds to 2.

**What it refuses.** Projected weights after orders fill (out of scope here), performance and returns, advice, any write (cancel, place, change), raw SQL, permission or tenant changes.

**What would change this recommendation.** If most of the questions turn out exploratory and open-ended rather than recurring, a constrained free-SQL path over the same `rpt` views and the same scoped login becomes worth its review cost. In a real integration, the measures should call the application's existing calculations rather than compute a competing version of drift or exposure.

## The audit trace

Each answer writes `runs/<run>/traces/<case>.json`: question; typed query and resolved interpretation; effective scope; prompt version and hash, user message, raw model reply, model id, recording id and CLI flags; catalog, compiler and schema versions; SQL and typed parameters; fixture id and SHA-256; as-of date, basis and reference clock; rows exactly as returned, contributing lines; status (complete, truncated, timeout, cancelled, error); real durations. `nlq export --trace <file> --out answer.xlsx` writes a workbook from that stored trace without running anything again: a Result sheet with typed numbers and units, the contributing lines, and an Audit sheet. Text that a spreadsheet could read as a formula is written as text with a quote prefix.

The fixture is frozen and hashed (`fixture/*.csv`) instead of using temporal tables. Reconstructing history in production (temporal tables, snapshots, or the application's own books of record) is an integration question.

## Database boundary

`db/003_security.sql`: a row-level security policy filters every tenant table on `SESSION_CONTEXT(N'tenant_id')`. The application sets it from the authenticated user with `sp_set_session_context ... @read_only = 1` and stops if it cannot read it back (`Sql/SessionScope.cs`). The reporting login can `SELECT` on schema `rpt` and nothing else. Tested directly, with no model involved (`tests/PortfolioNlq.SqlTests`): authorized rows returned; the other tenant's rows excluded even when named; a missing identity fails the query (it does not return an empty answer); changing the context fails; a reused pooled connection (same SPID) keeps the right scope; aggregates and joins stay filtered; inserts, updates, deletes, reads of `dbo`, policy changes and DDL all fail; name lookup runs under the same scope. `ApplicationIntent=ReadOnly` routes to a readable replica; it is not treated as a guarantee of anything here.

This is **tenant isolation**. Entitlements per account or per advisor inside a tenant are a second predicate on the same pattern, to be mapped to the client's own permission model.

## Where a client plugs in

- **Identity and entitlements**: `ScopeIdentity` is handed over by the host after authentication; extend `sec.fn_tenant_predicate` with account entitlements.
- **Schema and metrics**: `db/002_reporting.sql` (views over the real tables) and `Sql/SqlCompiler.cs` (measures); the catalog in `Catalog/Catalog.cs`.
- **Model**: any `IChatClient` through `ChatClientTranslator`; Azure OpenAI is wired in `src/PortfolioNlq.Cli/Program.cs` with `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT`, `AZURE_OPENAI_API_KEY` (or Entra ID through `DefaultAzureCredential`) and `NLQ_MODEL_NAME`.
- **Logs**: `AnswerTrace` is plain JSON; send it to the existing log store instead of `runs/`.
- **Hosting**: `PortfolioNlq.Core` targets `net8.0` and `net10.0` and has no host dependency, so it can run in process in an existing app or behind an internal service. The CLI host targets .NET 10 LTS (.NET 8 support ends on 10 November 2026).
- **Azure SQL**: uses only features available in Azure SQL Database (row-level security, `SESSION_CONTEXT`, inline functions, `STRING_AGG`). It was not run on Azure.

## How the model replies were recorded

`nlq record` calls `claude -p` with a fixed system prompt (`recordings/<id>/system_prompt.txt`, hash in the manifest), `--tools ""`, `--strict-mcp-config` with no servers, `--setting-sources ""`, `--disable-slash-commands`, `--no-session-persistence`, from an empty directory. Expected results are never in the context. Every attempt is kept with the CLI's raw output; a retry happens only if the CLI itself fails. The cost figures inside that raw output are list-price estimates printed by the CLI; the calls ran on a subscription and the repository needs no key to replay them.

## Layout

```
fixture/                 frozen synthetic data (CSV) and its traps
eval/cases.json          28 questions, sets (tuning, holdout, regression) and why each exists
eval/expected.json       results computed by src/PortfolioNlq.Reference before any model call
recordings/              raw model replies, every attempt
runs/                    traces and graded summaries; runs/PUBLISHED names the published pair
db/                      schema, reporting views, row-level security
src/PortfolioNlq.Core    catalog, validator, compiler, scoped execution, trace, Excel export, grading
src/PortfolioNlq.Cli     the `nlq` host
tests/                   unit tests (no database) and SQL Server tests
```

Data, names and prices are synthetic. Account, fund and security names are invented; custodian names are used only as labels.
