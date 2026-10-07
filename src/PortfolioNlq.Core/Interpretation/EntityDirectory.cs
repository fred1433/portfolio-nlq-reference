using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using PortfolioNlq.Fixture;
using PortfolioNlq.Sql;

namespace PortfolioNlq.Interpretation;

public sealed record DirectoryEntry(int Id, string Name, string? Code);

/// <summary>Names and values visible to one identity. Loaded through the same scope as the answer itself.</summary>
public sealed record DirectorySnapshot(
    IReadOnlyList<DirectoryEntry> Accounts,
    IReadOnlyList<DirectoryEntry> Households,
    IReadOnlyList<DirectoryEntry> Custodians,
    IReadOnlyList<DirectoryEntry> Models,
    IReadOnlyList<DirectoryEntry> Securities,
    IReadOnlyList<string> AssetClasses,
    IReadOnlyList<string> Tags,
    IReadOnlyList<DateOnly> CloseDates,
    string Source);

public interface IEntityDirectory
{
    Task<DirectorySnapshot> LoadAsync(ScopeIdentity scope, CancellationToken ct);
}

/// <summary>Reads the directory from the rpt views on a connection whose tenant is pinned in SESSION_CONTEXT.</summary>
public sealed class SqlEntityDirectory(string connectionString) : IEntityDirectory
{
    public async Task<DirectorySnapshot> LoadAsync(ScopeIdentity scope, CancellationToken ct)
    {
        await using var conn = await SessionScope.OpenAsync(connectionString, scope, ct);
        async Task<List<DirectoryEntry>> Q(string sql)
        {
            var list = new List<DirectoryEntry>();
            await using var cmd = new SqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return list;
        }
        async Task<List<T>> L<T>(string sql, Func<SqlDataReader, T> read)
        {
            var list = new List<T>();
            await using var cmd = new SqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(read(r));
            return list;
        }
        return new DirectorySnapshot(
            await Q("SELECT account_id, account_name, account_number FROM rpt.v_accounts ORDER BY account_id"),
            await Q("SELECT household_id, household, NULL FROM rpt.v_households ORDER BY household_id"),
            await Q("SELECT custodian_id, custodian, NULL FROM rpt.v_custodians_in_scope ORDER BY custodian_id"),
            await Q("SELECT model_id, model, NULL FROM rpt.v_models ORDER BY model_id"),
            await Q("SELECT security_id, security_name, symbol FROM rpt.v_securities ORDER BY security_id"),
            await L("SELECT DISTINCT asset_class FROM rpt.v_securities ORDER BY asset_class", r => r.GetString(0)),
            await L("SELECT DISTINCT tag FROM rpt.v_security_tags ORDER BY tag", r => r.GetString(0)),
            await L("SELECT close_date FROM rpt.v_close_dates ORDER BY close_date", r => DateOnly.FromDateTime(r.GetDateTime(0))),
            "sql: rpt views under SESSION_CONTEXT tenant " + scope.TenantId);
    }
}

/// <summary>
/// Database-free stand-in used by the verification mode only: it filters the fixture by tenant in memory.
/// It is labelled as such in every output; row-level security is not exercised by it.
/// </summary>
public sealed class FixtureEntityDirectory(FixtureSet f) : IEntityDirectory
{
    public Task<DirectorySnapshot> LoadAsync(ScopeIdentity scope, CancellationToken ct)
    {
        var accounts = f.Accounts.Where(a => a.TenantId == scope.TenantId).ToList();
        return Task.FromResult(new DirectorySnapshot(
            accounts.Select(a => new DirectoryEntry(a.AccountId, a.Name, a.AccountNumber)).ToList(),
            f.Households.Where(h => h.TenantId == scope.TenantId).Select(h => new DirectoryEntry(h.HouseholdId, h.Name, null)).ToList(),
            f.Custodians.Where(c => accounts.Any(a => a.CustodianId == c.CustodianId)).Select(c => new DirectoryEntry(c.CustodianId, c.Name, null)).ToList(),
            f.Models.Where(m => m.TenantId == scope.TenantId).Select(m => new DirectoryEntry(m.ModelId, m.Name, null)).ToList(),
            f.Securities.Select(s => new DirectoryEntry(s.SecurityId, s.Name, s.Symbol)).ToList(),
            f.Securities.Select(s => s.AssetClass).Distinct().Order(StringComparer.Ordinal).ToList(),
            f.SecurityTags.Select(t => t.Tag).Distinct().Order(StringComparer.Ordinal).ToList(),
            f.Prices.Select(p => p.PriceDate).Distinct().Order().ToList(),
            "fixture in memory, tenant " + scope.TenantId + " (row-level security not executed)"));
    }
}

/// <summary>Deterministic name matching: exact name or code first, then every word of the input present in the name.</summary>
public static partial class NameMatcher
{
    static readonly HashSet<string> Noise = ["the", "account", "accounts", "fund", "household", "family", "group", "acct", "s", "for", "of"];

    public static List<DirectoryEntry> Match(string input, IReadOnlyList<DirectoryEntry> candidates)
    {
        var norm = Normalize(input);
        var exact = candidates.Where(c => Normalize(c.Name) == norm || (c.Code is not null && Normalize(c.Code) == norm)).ToList();
        if (exact.Count > 0) return exact;
        var words = Words(input).Where(w => !Noise.Contains(w)).ToList();
        if (words.Count == 0) return [];
        return candidates.Where(c =>
        {
            var have = Words(c.Name).Concat(c.Code is null ? [] : Words(c.Code)).ToHashSet();
            return words.All(have.Contains);
        }).ToList();
    }

    static string Normalize(string s) => string.Join(' ', Words(s));

    static IEnumerable<string> Words(string s) =>
        WordRegex().Matches(s.ToLowerInvariant().Replace("'s", "").Replace("’s", "")).Select(m => m.Value);

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();
}
