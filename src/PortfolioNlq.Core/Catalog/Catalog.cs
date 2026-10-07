namespace PortfolioNlq.Catalog;

/// <summary>
/// The reviewed vocabulary. The model may only choose among these measures, fields and values;
/// the formulas and joins behind them live in <see cref="Sql.SqlCompiler"/> and db/002_reporting.sql.
/// </summary>
public static class QueryCatalog
{
    public const string Version = "catalog-2026.10.1";

    public const string Drift = "drift";
    public const string OpenAllocations = "open_allocations";

    public static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>
    {
        [Drift] = "Weight = market value of the sleeve's positions / market value of the whole account, cash included, in USD at the as-of close. " +
                  "Target = the account model's weight for the sleeve, multiplied down the model hierarchy. Drift = weight minus target, in percentage points. " +
                  "An account with any position lacking a close price or an FX rate on the as-of date is reported as incomplete, never valued at zero.",
        [OpenAllocations] = "Per allocation of a block order: allocated quantity, executed quantity (sum of fills up to the as-of time), cancelled quantity, " +
                            "remaining = allocated - executed - cancelled. Open means remaining above zero. Quantities in shares or units; no prices involved.",
    };

    public static readonly string[] DriftGroupBy = ["account", "household"];
    public static readonly string[] AllocationGroupBy = ["account", "security", "custodian", "block"];
    public static readonly string[] DriftFilters = ["account", "household", "custodian", "model"];
    public static readonly string[] AllocationFilters = ["account", "household", "custodian", "security"];
    public static readonly string[] Relative = ["today", "yesterday", "last_close", "last_friday", "previous_month_end",
        "monday", "tuesday", "wednesday", "thursday", "friday"];

    /// <summary>Approved defaults. Each one used is written into the answer and the trace.</summary>
    public static class Defaults
    {
        public const string ReportingCurrency = "USD";
        public const string TimeZone = "America/Chicago";
        public const string DateBasis = "trade";
        public const string AllocationStatus = "open";
        public static readonly string[] AllocationGroupBy = ["account", "security", "custodian"];
        public const string LastWeek = "Monday to Friday of the previous calendar week";
    }
}
