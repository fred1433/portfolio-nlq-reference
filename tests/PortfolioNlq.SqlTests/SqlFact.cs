using Microsoft.Data.SqlClient;

namespace PortfolioNlq.SqlTests;

/// <summary>Runs only when a SQL Server is configured (make sql sets both variables). Otherwise reported as skipped, never as passed.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Db.Reader) || string.IsNullOrEmpty(Db.Admin))
            Skip = "SQL Server not configured (NLQ_ADMIN_CONNECTION, NLQ_READER_CONNECTION): run `make sql`";
    }
}

public static class Db
{
    public static string? Admin => Environment.GetEnvironmentVariable("NLQ_ADMIN_CONNECTION");
    public static string? Reader => Environment.GetEnvironmentVariable("NLQ_READER_CONNECTION");

    /// <summary>The reporting login's connection string pointed at another database (used for perturbation copies).</summary>
    public static string ReaderOn(string database) => new SqlConnectionStringBuilder(Reader) { InitialCatalog = database }.ConnectionString;

    public static string ReaderLogin => new SqlConnectionStringBuilder(Reader).UserID;
    public static string ReaderPassword => new SqlConnectionStringBuilder(Reader).Password;
}
