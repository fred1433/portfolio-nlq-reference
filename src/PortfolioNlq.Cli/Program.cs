using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using PortfolioNlq.Audit;
using PortfolioNlq.Catalog;
using PortfolioNlq.Cli;
using PortfolioNlq.Export;
using PortfolioNlq.Interpretation;
using PortfolioNlq.Reference;
using PortfolioNlq.Sql;

var argv = args.ToList();
string? Opt(string name) { var i = argv.IndexOf(name); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
string Req(string name) => Opt(name) ?? throw new ArgumentException($"missing {name}");
string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new ArgumentException($"environment variable {name} is not set");

try
{
    switch (argv.FirstOrDefault())
    {
        case "prompt":
            Console.WriteLine(PromptBuilder.SystemPrompt());
            Console.Error.WriteLine($"{PromptBuilder.Version} sha256 {PromptBuilder.Sha256()}");
            return 0;

        case "expected":
            return Commands.Expected();

        case "db-setup":
            await DbSetup.RunAsync(Env("NLQ_ADMIN_CONNECTION"), Opt("--database") ?? "NlqReference", Opt("--login") ?? "nlq_reader", Env("NLQ_READER_PASSWORD"), Repo.Fixture());
            Console.WriteLine("database created, fixture loaded, reporting login ready");
            return 0;

        case "record":
            await Recorder.RunAsync(Req("--out"), Opt("--model") ?? "sonnet", Opt("--only")?.Split(',').ToHashSet(), Opt("--note"));
            return 0;

        case "replay":
            return await Commands.Replay(Req("--recording"), Req("--run-id"), Req("--out"), argv.Contains("--sql") ? Env("NLQ_READER_CONNECTION") : null);

        case "verify":
            return await Commands.Verify(Req("--recording"), Req("--run"));

        case "compare":
            return Commands.Compare(Req("--a"), Req("--b"));

        case "export":
            ExcelExporter.Write(AnswerTrace.Load(Req("--trace")), Req("--out"));
            Console.WriteLine("workbook written from the stored trace, nothing re-executed: " + Req("--out"));
            return 0;

        case "ask":
        {
            // Fresh call. Explicit only: needs a provider configuration and a database. Never run by tests or CI.
            var provider = Opt("--provider") ?? Env("NLQ_PROVIDER");
            IChatClient client = provider switch
            {
                "azure-openai" => (Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") is { Length: > 0 } key
                        ? new AzureOpenAIClient(new Uri(Env("AZURE_OPENAI_ENDPOINT")), new AzureKeyCredential(key))
                        : new AzureOpenAIClient(new Uri(Env("AZURE_OPENAI_ENDPOINT")), new DefaultAzureCredential()))
                    .GetChatClient(Env("AZURE_OPENAI_DEPLOYMENT")).AsIChatClient(),
                _ => throw new ArgumentException("--provider azure-openai is the wired provider; any other IChatClient can be passed to ChatClientTranslator"),
            };
            var eval = EvalFile.Load(Repo.CasesFile);
            var clock = argv.Contains("--live-clock") ? new ReferenceClock(DateTimeOffset.Now, eval.Timezone) : ReferenceClock.Parse(eval.ReferenceClock, eval.Timezone);
            var f = Repo.Fixture();
            var reader = Env("NLQ_READER_CONNECTION");
            var pipeline = new AnswerPipeline(new ChatClientTranslator(client, provider, Environment.GetEnvironmentVariable("NLQ_MODEL_NAME") ?? Env("AZURE_OPENAI_DEPLOYMENT")),
                new SqlEntityDirectory(reader), new ScopedQueryExecutor(reader),
                new PipelineContext("ask-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"), clock, new FixtureRef { Id = f.FixtureId, Sha256 = f.Sha256 }, Repo.Versions(), Repo.Platform()));
            var trace = await pipeline.AnswerAsync("ask", Req("--question"), new ScopeIdentity(int.Parse(Req("--tenant")), Opt("--user") ?? "cli-user"), CancellationToken.None);
            Console.WriteLine(trace.ToJson());
            return trace.Outcome == "error" ? 1 : 0;
        }

        case "page-data":
            return PageData.Write(Req("--run"), Req("--recording"), Req("--out"), Opt("--before-run"), Opt("--before-recording"));

        default:
            Console.WriteLine("""
nlq <command>
  prompt                                 print the fixed system prompt and its hash
  expected                               compute eval/expected.json from the fixture (independent reference)
  db-setup                               create the database, load the fixture, create the reporting login
                                         (NLQ_ADMIN_CONNECTION, NLQ_READER_PASSWORD)
  record --out <dir> [--model sonnet]    record model replies with `claude -p` (subscription, no API key)
  replay --recording <dir> --run-id <id> --out <dir> [--sql]
                                         answer every case from the recordings; --sql runs on SQL Server (NLQ_READER_CONNECTION)
  verify --recording <dir> --run <dir>   no database: hashes, replies -> SQL, stored rows re-graded
  compare --a <run> --b <run>            same answers in two runs?
  export --trace <file> --out <xlsx>     Excel workbook from a stored trace, nothing re-executed
  ask --tenant <id> --question <text>    fresh model call (NLQ_PROVIDER=azure-openai, AZURE_OPENAI_*), explicit only
""");
            return argv.Count == 0 ? 0 : 2;
    }
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
