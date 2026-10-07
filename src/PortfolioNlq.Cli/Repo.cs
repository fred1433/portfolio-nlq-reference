using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PortfolioNlq.Audit;
using PortfolioNlq.Catalog;
using PortfolioNlq.Fixture;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Cli;

/// <summary>Paths and versions shared by the commands.</summary>
public static class Repo
{
    public static string Root { get; } = FindRoot();
    public static string FixtureDir => Path.Combine(Root, "fixture");
    public static string DbDir => Path.Combine(Root, "db");
    public static string CasesFile => Path.Combine(Root, "eval", "cases.json");
    public static string ExpectedFile => Path.Combine(Root, "eval", "expected.json");

    static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("NLQ_REPO_ROOT");
        if (!string.IsNullOrEmpty(env)) return env;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var d = new DirectoryInfo(start);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "PortfolioNlq.slnx"))) d = d.Parent;
            if (d is not null) return d.FullName;
        }
        throw new InvalidOperationException("Run from inside the repository (PortfolioNlq.slnx not found).");
    }

    public static string SchemaVersion()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.GetFiles(DbDir, "*.sql").Order(StringComparer.Ordinal))
            sha.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(f).Replace("\r\n", "\n")));
        return "schema-" + Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..12];
    }

    public static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n")))).ToLowerInvariant();

    public static Versions Versions() => new()
    {
        Catalog = QueryCatalog.Version,
        Compiler = SqlCompiler.Version,
        Schema = SchemaVersion(),
        Prompt = PromptBuilder.Version + " sha256 " + PromptBuilder.Sha256()[..12],
        App = "PortfolioNlq " + (typeof(Repo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev"),
    };

    public static FixtureSet Fixture() => FixtureSet.Load(FixtureDir);

    public static string Platform() => $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}";
}
