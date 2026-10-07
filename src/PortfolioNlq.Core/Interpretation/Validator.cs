using System.Globalization;
using PortfolioNlq.Catalog;

namespace PortfolioNlq.Interpretation;

public sealed record ResolvedFilter(string Field, string Input, IReadOnlyList<int> Ids, IReadOnlyList<string> Matched);

/// <summary>A typed query whose every name is an id visible to the asking identity and whose every date is a calendar date.</summary>
public sealed record ResolvedQuery(
    string Measure,
    string? DimensionType,
    string? DimensionValue,
    string? ThresholdDirection,
    decimal? ThresholdPoints,
    IReadOnlyList<string> GroupBy,
    string DateBasis,
    DateOnly? AsOf,
    DateTime AsOfTime,
    string? Side,
    string? AssetClass,
    string Status,
    IReadOnlyList<ResolvedFilter> Filters,
    IReadOnlyList<string> DefaultsApplied,
    string Definition);

public enum OutcomeKind { Query, Clarify, Refuse, Rejected, NoMatchInScope }

public sealed record ValidationOutcome(OutcomeKind Kind, ResolvedQuery? Query, string? Message, string Source)
{
    public static ValidationOutcome Reject(string why) => new(OutcomeKind.Rejected, null, why, "validator");
    public static ValidationOutcome Ask(string why) => new(OutcomeKind.Clarify, null, why, "validator");
}

/// <summary>
/// Checks the model's typed query against the catalog and resolves it. It never fills a gap by guessing:
/// a missing financial choice becomes a clarification, an unknown shape becomes a rejection.
/// </summary>
public static class Validator
{
    public static ValidationOutcome Validate(ModelOutput o, DirectorySnapshot dir, ReferenceClock clock)
    {
        switch (o.Kind)
        {
            case "clarify":
                return string.IsNullOrWhiteSpace(o.Message) ? ValidationOutcome.Reject("clarify without a question") : new(OutcomeKind.Clarify, null, o.Message, "model");
            case "refuse":
                return string.IsNullOrWhiteSpace(o.Message) ? ValidationOutcome.Reject("refuse without a reason") : new(OutcomeKind.Refuse, null, o.Message, "model");
            case "query": break;
            default: return ValidationOutcome.Reject($"unknown kind '{o.Kind}'");
        }

        var defaults = new List<string> { $"reporting currency {QueryCatalog.Defaults.ReportingCurrency}", $"dates and timestamps in {QueryCatalog.Defaults.TimeZone}" };
        return o.Measure switch
        {
            QueryCatalog.Drift => Drift(o, dir, clock, defaults),
            QueryCatalog.OpenAllocations => Allocations(o, dir, clock, defaults),
            _ => ValidationOutcome.Reject($"unknown measure '{o.Measure}'"),
        };
    }

    static ValidationOutcome Drift(ModelOutput o, DirectorySnapshot dir, ReferenceClock clock, List<string> defaults)
    {
        if (o.Side is not null || o.Status is not null || o.AssetClass is not null)
            return ValidationOutcome.Reject("side, status and asset_class are not fields of drift (use dimension)");
        if (o.Dimension?.Type is not ("asset_class" or "tag") || string.IsNullOrWhiteSpace(o.Dimension.Value))
            return ValidationOutcome.Ask("Drift of which sleeve: an asset class (Equity, Fixed Income, Cash) or a tag?");
        var pool = o.Dimension.Type == "asset_class" ? dir.AssetClasses : dir.Tags;
        var dimValue = pool.FirstOrDefault(v => string.Equals(v, o.Dimension.Value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (dimValue is null)
            return ValidationOutcome.Ask($"'{o.Dimension.Value}' is not a known {o.Dimension.Type.Replace('_', ' ')}. Known values: {string.Join(", ", pool)}.");

        string? direction = null; decimal? points = null;
        if (o.Threshold is not null)
        {
            if (o.Threshold.Direction is not ("above" or "below" or "either")) return ValidationOutcome.Reject("threshold direction must be above, below or either");
            if (o.Threshold.Points is not > 0 and not null || o.Threshold.Points > 100) return ValidationOutcome.Reject("threshold points must be between 0 and 100");
            if (o.Threshold.Points is null) return ValidationOutcome.Ask("How many points of drift should count?");
            direction = o.Threshold.Direction; points = o.Threshold.Points;
        }

        var groupBy = o.GroupBy is null or { Count: 0 } ? ["account"] : o.GroupBy.Select(g => g.Trim().ToLowerInvariant()).ToList();
        if (groupBy.Count != 1 || !QueryCatalog.DriftGroupBy.Contains(groupBy[0])) return ValidationOutcome.Reject("drift groups by account or by household");

        var basis = o.DateBasis ?? QueryCatalog.Defaults.DateBasis;
        if (basis is not ("trade" or "settlement")) return ValidationOutcome.Reject("date_basis must be trade or settlement");
        if (o.DateBasis is null) defaults.Add("trade-date positions");

        var (asOf, dateProblem) = ResolveDate(o.AsOf, dir, clock, defaults);
        if (dateProblem is not null) return ValidationOutcome.Ask(dateProblem);

        var (filters, filterOutcome) = ResolveFilters(o.Filters, QueryCatalog.DriftFilters, dir);
        var q = new ResolvedQuery(QueryCatalog.Drift, o.Dimension.Type, dimValue, direction, points, groupBy, basis, asOf,
            asOf!.Value.ToDateTime(new TimeOnly(23, 59, 59)), null, null, "n/a", filters, defaults, QueryCatalog.Definitions[QueryCatalog.Drift]);
        return filterOutcome is null ? new(OutcomeKind.Query, q, null, "validator") : filterOutcome with { Query = filterOutcome.Kind == OutcomeKind.NoMatchInScope ? q : null };
    }

    static ValidationOutcome Allocations(ModelOutput o, DirectorySnapshot dir, ReferenceClock clock, List<string> defaults)
    {
        if (o.Dimension is not null || o.Threshold is not null || o.DateBasis is not null)
            return ValidationOutcome.Reject("dimension, threshold and date_basis are not fields of open_allocations");
        string? side = o.Side?.Trim().ToLowerInvariant() switch { null => null, "buy" => "Buy", "sell" => "Sell", _ => "?" };
        if (side == "?") return ValidationOutcome.Reject("side must be buy or sell");
        string? assetClass = null;
        if (o.AssetClass is not null)
        {
            assetClass = dir.AssetClasses.FirstOrDefault(v => string.Equals(v, o.AssetClass.Trim(), StringComparison.OrdinalIgnoreCase));
            if (assetClass is null) return ValidationOutcome.Ask($"'{o.AssetClass}' is not a known asset class. Known: {string.Join(", ", dir.AssetClasses)}.");
        }
        var status = o.Status ?? QueryCatalog.Defaults.AllocationStatus;
        if (status is not ("open" or "all")) return ValidationOutcome.Reject("status must be open or all");
        if (o.Status is null) defaults.Add("open allocations only (remaining above zero)");

        var groupBy = o.GroupBy is null or { Count: 0 } ? QueryCatalog.Defaults.AllocationGroupBy.ToList() : o.GroupBy.Select(g => g.Trim().ToLowerInvariant()).Distinct().ToList();
        if (groupBy.Any(g => !QueryCatalog.AllocationGroupBy.Contains(g))) return ValidationOutcome.Reject("allocations group by account, security, custodian or block");
        if (o.GroupBy is null or { Count: 0 }) defaults.Add("one row per account, security and custodian");

        DateOnly? asOf = null; DateTime asOfTime;
        if (o.AsOf is null)
        {
            asOfTime = clock.LocalNow;
            defaults.Add($"order book as at the reference time {clock.LocalNow:yyyy-MM-dd HH:mm} {clock.TimeZone}");
        }
        else
        {
            var (d, problem) = ResolveCalendarDate(o.AsOf, clock, dir.CloseDates);
            if (problem is not null) return ValidationOutcome.Ask(problem);
            asOf = d;
            var endOfDay = d!.Value.ToDateTime(new TimeOnly(23, 59, 59));
            asOfTime = endOfDay < clock.LocalNow ? endOfDay : clock.LocalNow; // a day not over yet is read up to the reference time
        }

        var (filters, filterOutcome) = ResolveFilters(o.Filters, QueryCatalog.AllocationFilters, dir);
        var q = new ResolvedQuery(QueryCatalog.OpenAllocations, null, null, null, null, groupBy, "n/a", asOf, asOfTime, side, assetClass, status,
            filters, defaults, QueryCatalog.Definitions[QueryCatalog.OpenAllocations]);
        return filterOutcome is null ? new(OutcomeKind.Query, q, null, "validator") : filterOutcome with { Query = filterOutcome.Kind == OutcomeKind.NoMatchInScope ? q : null };
    }

    static (DateOnly? Date, string? Problem) ResolveDate(DateOut? spec, DirectorySnapshot dir, ReferenceClock clock, List<string> defaults)
    {
        if (spec is null)
        {
            var before = dir.CloseDates.Where(c => c < clock.Today).ToList();
            if (before.Count == 0) return (null, "No close is recorded before the reference date.");
            var d = before.Max();
            defaults.Add($"latest close before the reference time: {d:yyyy-MM-dd}");
            return (d, null);
        }
        var (date, problem) = ResolveCalendarDate(spec, clock, dir.CloseDates);
        if (problem is not null) return (null, problem);
        if (!dir.CloseDates.Contains(date!.Value))
            return (null, $"No closing prices are recorded for {date:yyyy-MM-dd} ({date.Value.DayOfWeek}). Recorded closes: {string.Join(", ", dir.CloseDates.Select(c => c.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}. Which date should I use?");
        return (date, null);
    }

    static (DateOnly? Date, string? Problem) ResolveCalendarDate(DateOut spec, ReferenceClock clock, IReadOnlyList<DateOnly> closes)
    {
        var forms = (spec.Date is not null ? 1 : 0) + (spec.Month is not null || spec.Day is not null ? 1 : 0) + (spec.Relative is not null ? 1 : 0);
        if (forms != 1) return (null, "Which date do you mean?");
        DateOnly date;
        if (spec.Date is not null)
        {
            if (!DateOnly.TryParseExact(spec.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return (null, $"'{spec.Date}' is not a date.");
        }
        else if (spec.Relative is not null)
        {
            var r = clock.Resolve(spec.Relative, closes);
            if (r is null) return (null, $"'{spec.Relative}' is not a date I can pin down. Which date do you mean?");
            date = r.Value;
        }
        else
        {
            if (spec.Month is not (>= 1 and <= 12) || spec.Day is null) return (null, "Which date do you mean?");
            try { date = new DateOnly(clock.Today.Year, spec.Month.Value, spec.Day.Value); }
            catch (ArgumentOutOfRangeException) { return (null, "That day does not exist. Which date do you mean?"); }
        }
        if (date > clock.Today) return (null, $"{date:yyyy-MM-dd} is after the reference date {clock.Today:yyyy-MM-dd}. Which date do you mean?");
        return (date, null);
    }

    static (List<ResolvedFilter> Filters, ValidationOutcome? Outcome) ResolveFilters(List<FilterOut>? input, string[] allowed, DirectorySnapshot dir)
    {
        var resolved = new List<ResolvedFilter>();
        var unmatched = new List<string>();
        foreach (var f in input ?? [])
        {
            var field = f.Field?.Trim().ToLowerInvariant();
            if (field is null || !allowed.Contains(field)) return (resolved, ValidationOutcome.Reject($"filter field '{f.Field}' is not allowed here"));
            if (string.IsNullOrWhiteSpace(f.Value)) return (resolved, ValidationOutcome.Reject($"filter '{field}' has no value"));
            var pool = field switch
            {
                "account" => dir.Accounts, "household" => dir.Households, "custodian" => dir.Custodians,
                "model" => dir.Models, "security" => dir.Securities, _ => throw new InvalidOperationException(field),
            };
            var hits = NameMatcher.Match(f.Value, pool);
            if (hits.Count > 1)
                return (resolved, ValidationOutcome.Ask($"'{f.Value}' matches {hits.Count} {field} records: {string.Join(", ", hits.Select(h => h.Code is null ? h.Name : $"{h.Name} ({h.Code})"))}. Which one?"));
            if (hits.Count == 0) { unmatched.Add($"{field} '{f.Value}'"); continue; }
            resolved.Add(new(field, f.Value, [hits[0].Id], [hits[0].Code is null ? hits[0].Name : $"{hits[0].Name} ({hits[0].Code})"]));
        }
        if (unmatched.Count > 0)
            return (resolved, new(OutcomeKind.NoMatchInScope, null,
                $"No {string.Join(", ", unmatched)} is visible to this user. Nothing was returned.", "validator"));
        return (resolved, null);
    }
}
